using System.Net.WebSockets;
using System.Text.Json;
using JsUnitySync.Contracts.Networking;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Networking;

namespace JsUnitySync.Desktop.Core.Realtime;

/// <summary>Authenticated WSS client. Accepted scene events are DPAPI-spooled before their sequence is acknowledged.</summary>
public sealed class RealtimeSessionClient(CollaborationClient collaboration, AccountViewModel account, IRealtimeClientStore store)
{
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(string sessionId, Action<string> status, CancellationToken cancellationToken)
    {
        var profile = account.Profile ?? throw new ApiClientException(401, "Sign in to join a session.");
        var server = collaboration.ServerAddress;
        var userId = profile.Id;
        var cursor = await Task.Run(() => store.GetOrCreateCursor(server, userId, sessionId), cancellationToken);
        var retries = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(server, userId, sessionId, cursor.ClientId, status, cancellationToken);
                throw new WebSocketException("Realtime connection closed; attempting recovery.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ApiClientException error) when (error.Status is >= 400 and < 500) { throw; }
            catch (RealtimeStorageException) { throw; }
            catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException)
            {
                retries++;
                if (retries > RetryPolicy.Default.MaxAttempts)
                    throw new ApiClientException(503, "Realtime server is still unavailable after 5 reconnect attempts. Retry the connection when ready.");
                var delay = RetryPolicy.Default.DelayBefore(retries)!.Value;
                status($"Connection interrupted ({error.GetType().Name}). Reconnecting in {delay.TotalMilliseconds:0} ms; accepted work will replay from sequence {cursor.LastAckedSequence}.");
                await Task.Delay(delay, cancellationToken);
                cursor = await Task.Run(() => store.GetOrCreateCursor(server, userId, sessionId), cancellationToken);
            }
        }
    }

    private async Task ConnectOnceAsync(string serverAddress, string userId, string sessionId, string clientId,
        Action<string> status, CancellationToken token)
    {
        var bearer = collaboration.RealtimeAccessToken;
        if (bearer is null) throw new ApiClientException(401, "Sign in again to join a collaboration session.");
        var origin = new Uri(serverAddress);
        var endpoint = new UriBuilder(origin)
        {
            Scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = $"realtime/v1/sessions/{Uri.EscapeDataString(sessionId)}",
            Query = "", Fragment = ""
        }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(endpoint, token);

        var cursor = store.GetOrCreateCursor(serverAddress, userId, sessionId);
        await SendAsync(socket, new WireFrame
        {
            Kind = FrameKind.Hello, SessionId = sessionId, UtcTimestamp = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new HelloPayload
            { UserId = userId, ClientId = clientId, ProtocolVersion = ProtocolInfo.Version, LastAckedSequence = cursor.LastAckedSequence }, Json)
        }, token);
        var welcomeFrame = await ReceiveAsync(socket, token);
        if (welcomeFrame.Kind != FrameKind.Welcome || welcomeFrame.SessionId != sessionId)
            throw new ApiClientException(502, "Realtime server did not complete the session handshake.");
        var welcome = JsonSerializer.Deserialize<SessionWelcomePayload>(welcomeFrame.PayloadJson, Json)
            ?? throw new ApiClientException(502, "Realtime server returned an invalid welcome frame.");
        if (welcome.ProtocolVersion != ProtocolInfo.Version || welcome.LastSequence < cursor.LastAckedSequence)
            throw new ApiClientException(409, "Realtime server state is inconsistent with the locally accepted event cursor.");
        status($"Connected to session. Replaying from locally durable sequence {cursor.LastAckedSequence}; scene changes are being safely queued for the Unity integration.");

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var frame = await ReceiveAsync(socket, token);
            if (frame.SessionId != sessionId) throw new ApiClientException(502, "Realtime frame session mismatch.");
            switch (frame.Kind)
            {
                case FrameKind.SceneEvent:
                {
                    long applied;
                    try { applied = await Task.Run(() => store.StoreAcceptedEventAsync(serverAddress, userId, sessionId, clientId, frame, token), token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    { throw new RealtimeStorageException("The protected local event inbox could not durably store this event. The server was not acknowledged; check local storage permissions and reconnect.", error); }
                    cursor = cursor with { LastAckedSequence = applied };
                    await SendAsync(socket, new WireFrame
                    {
                        Kind = FrameKind.EventAck, SessionId = sessionId, UtcTimestamp = DateTimeOffset.UtcNow,
                        PayloadJson = JsonSerializer.Serialize(new EventAckPayload { HighestAppliedSequence = applied }, Json)
                    }, token);
                    var count = store.GetSnapshot(serverAddress, userId, sessionId).AcceptedEventCount;
                    status($"Connected · {count:N0} durable scene event(s) queued locally. Scene changes are not applied to Unity until its Editor integration is active.");
                    break;
                }
                case FrameKind.Conflict:
                    try { await Task.Run(() => store.StoreConflictAsync(serverAddress, userId, sessionId, clientId, frame, token), token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    { throw new RealtimeStorageException("A rejected conflict could not be preserved in the protected local inbox.", error); }
                    status("A conflicting property write was rejected by the server and preserved in the protected conflict inbox. Refresh/reconcile before continuing.");
                    break;
                case FrameKind.Heartbeat:
                    break;
                case FrameKind.Error:
                {
                    var error = JsonSerializer.Deserialize<ErrorPayload>(frame.PayloadJson, Json);
                    if (error?.Code == "service_unavailable")
                        throw new WebSocketException("Realtime service interrupted; reconnecting to recover durable events.");
                    throw new ApiClientException(409, error?.Message ?? "Realtime server rejected the connection.");
                }
                default:
                    throw new ApiClientException(502, "Realtime server sent an unsupported frame kind.");
            }
        }
        throw new WebSocketException("Realtime connection closed.");
    }

    private static Task SendAsync(ClientWebSocket socket, WireFrame frame, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Realtime frame exceeds 64 KiB.");
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).AsTask();
    }

    private static async Task<WireFrame> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var data = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Realtime server closed the session.");
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Realtime server sent a non-text frame.");
            if (data.Length + result.Count > MaxFrameBytes) throw new InvalidDataException("Realtime server frame exceeds 64 KiB.");
            data.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<WireFrame>(data.ToArray(), Json)
            ?? throw new InvalidDataException("Realtime server frame was empty.");
    }
}
