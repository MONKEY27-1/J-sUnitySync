using System.Buffers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Server.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JsUnitySync.Server.Realtime;

/// <summary>One authorized WebSocket connection. The database log is authoritative; pub/sub only accelerates delivery.</summary>
public sealed class RealtimeSocketService(
    CollaborationSessionService sessions,
    RealtimeEventService events,
    IRealtimeBackplane backplane,
    ILogger<RealtimeSocketService> logger)
{
    private const int MaxFrameBytes = 64 * 1024;
    private const int ReplayBatch = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(HttpContext context, string sessionId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || !JsUnitySync.Domain.Ids.StableId.TryParse(sessionId, out _))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        SessionSummary session;
        try { session = await sessions.GetAuthorizedAsync(userId, sessionId, context.RequestAborted); }
        catch (ApiFault fault) { context.Response.StatusCode = fault.Status; return; }
        if (!backplane.IsHealthy)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            await using var subscription = await backplane.SubscribeAsync(sessionId, connection.Token);
            var first = await ReceiveFrameAsync(webSocket, connection.Token);
            if (first is null || first.Kind != FrameKind.Hello || first.SessionId != sessionId)
                throw new ApiFault(400, "hello_required", "The first frame must be a valid session Hello.");
            var hello = Deserialize<HelloPayload>(first.PayloadJson);
            if (hello.ProtocolVersion != ProtocolInfo.Version)
                throw new ApiFault(400, "protocol_version", "Realtime protocol version is not supported.");
            if (hello.UserId != userId)
                throw ApiFault.Missing();
            if (hello.LastAckedSequence < -1)
                throw ApiFault.Invalid("Invalid acknowledged sequence.");
            var scene = await sessions.BindSceneAsync(userId, sessionId, hello.SceneGuid, hello.SceneManifestHash, connection.Token);
            var cursor = await events.EstablishCursorAsync(userId, sessionId, hello.ClientId, hello.LastAckedSequence, connection.Token);
            var lastSequence = await events.LastSequenceAsync(userId, sessionId, connection.Token);
            await SendFrameAsync(webSocket, new WireFrame
            {
                Kind = FrameKind.Welcome, SessionId = sessionId, UtcTimestamp = DateTimeOffset.UtcNow,
                PayloadJson = JsonSerializer.Serialize(new SessionWelcomePayload
                { ProtocolVersion = ProtocolInfo.Version, SceneGuid = scene.SceneGuid, SceneManifestHash = scene.ManifestHash, LastSequence = lastSequence, ReplayFromSequence = cursor }, Json)
            }, sendGate, connection.Token);

            var sender = SendEventsAsync(webSocket, subscription.Reader, userId, sessionId, cursor, sendGate, connection.Token);
            var receiver = ReceiveCommandsAsync(webSocket, userId, sessionId, hello.ClientId, sendGate, connection.Token);
            var completed = await Task.WhenAny(sender, receiver);
            connection.Cancel();
            await sendGate.WaitAsync(CancellationToken.None);
            try { await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "connection ended", CancellationToken.None); }
            catch (WebSocketException) { }
            finally { sendGate.Release(); }
            try { await Task.WhenAll(sender, receiver); }
            catch (OperationCanceledException) { }
            catch (WebSocketException error) { logger.LogDebug("Realtime peer disconnected with {ErrorType}", error.GetType().Name); }
            _ = completed;
        }
        catch (ApiFault fault)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await SendFrameAsync(webSocket, Error(fault.Code, fault.Message, sessionId), sendGate, CancellationToken.None);
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "protocol rejected", CancellationToken.None);
                }
                catch (WebSocketException) { }
            }
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            logger.LogWarning("Realtime frame rejected with {ErrorType}", error.GetType().Name);
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await SendFrameAsync(webSocket, Error(error is JsonException ? "invalid_json" : "invalid_format", "Realtime frame is malformed or exceeds its allowed shape.", sessionId), sendGate, CancellationToken.None);
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid frame", CancellationToken.None);
                }
                catch (WebSocketException) { }
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
        catch (WebSocketException error)
        {
            logger.LogDebug("Realtime connection closed with {ErrorType}", error.GetType().Name);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Realtime connection failed with {ErrorType}", error.GetType().Name);
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await SendFrameAsync(webSocket, Error("service_unavailable", "Realtime service temporarily unavailable; reconnect to replay accepted work.", sessionId), sendGate, CancellationToken.None);
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "reconnect to recover", CancellationToken.None);
                }
                catch (WebSocketException) { }
            }
        }
    }

    private async Task ReceiveCommandsAsync(WebSocket socket, string userId, string sessionId, string clientId, SemaphoreSlim sendGate, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var frame = await ReceiveFrameAsync(socket, token);
            if (frame is null) return;
            if (frame.SessionId != sessionId) throw ApiFault.Invalid("Frame session does not match the joined room.");
            switch (frame.Kind)
            {
                case FrameKind.SceneEvent:
                {
                    if (frame.Sequence != -1) throw ApiFault.Invalid("Clients cannot assign scene event sequences.");
                    var mutation = Deserialize<SceneMutation>(frame.PayloadJson);
                    var result = await events.AppendAsync(userId, sessionId, frame.FrameId, mutation, token);
                    if (result.Conflict is not null)
                    {
                        await backplane.PublishAsync(sessionId, result.Conflict, token);
                        break;
                    }
                    await backplane.PublishAsync(sessionId, result.Event!, token);
                    logger.LogInformation("Accepted realtime event {EventId} for {SessionId} at {Sequence}", result.Event!.FrameId, sessionId, result.Event.Sequence);
                    break;
                }
                case FrameKind.EventAck:
                {
                    var ack = Deserialize<EventAckPayload>(frame.PayloadJson);
                    await events.AcknowledgeAsync(userId, sessionId, clientId, ack.HighestAppliedSequence, token);
                    break;
                }
                case FrameKind.Heartbeat:
                    await SendFrameAsync(socket, new WireFrame { Kind = FrameKind.Heartbeat, SessionId = sessionId, UtcTimestamp = DateTimeOffset.UtcNow }, sendGate, token);
                    break;
                case FrameKind.ResumeRequest:
                    await SendFrameAsync(socket, Error("reconnect_to_resume", "Reconnect using your last durably applied sequence to resume delivery.", sessionId), sendGate, token);
                    break;
                default:
                    throw ApiFault.Invalid("Frame kind is not accepted from a session client.");
            }
        }
    }

    private async Task SendEventsAsync(WebSocket socket, System.Threading.Channels.ChannelReader<WireFrame> notifications,
        string userId, string sessionId, long lastSent, SemaphoreSlim sendGate, CancellationToken token)
    {
        var sentConflictIds = new HashSet<string>(StringComparer.Ordinal);
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var notification = notifications.WaitToReadAsync(wait.Token).AsTask();
                var tick = Task.Delay(TimeSpan.FromSeconds(1), wait.Token);
                await Task.WhenAny(notification, tick);
                wait.Cancel();
                try { if (!await notification) return; } catch (OperationCanceledException) { }
                try { await tick; } catch (OperationCanceledException) { }
            }
            // Pub/sub is deliberately just a wake-up signal. Read sequentially from the
            // durable store to repair drops, reordering, reconnect gaps and Redis outages.
            var batch = await events.ReadAfterAsync(userId, sessionId, lastSent, ReplayBatch, token);
            foreach (var frame in batch)
            {
                if (frame.Sequence <= lastSent) continue;
                var expectedSequence = lastSent < 0 ? 1 : lastSent + 1;
                if (frame.Sequence != expectedSequence)
                    throw new InvalidOperationException("Durable session event sequence contains a gap.");
                await SendFrameAsync(socket, frame, sendGate, token);
                lastSent = frame.Sequence;
            }
            var conflicts = await events.RecentConflictsAsync(userId, sessionId, token);
            foreach (var conflict in conflicts)
                if (sentConflictIds.Add(conflict.FrameId)) await SendFrameAsync(socket, conflict, sendGate, token);
            while (notifications.TryRead(out _)) { } // coalesce hints; durable queries above are authoritative
        }
    }

    private static async Task<WireFrame?> ReceiveFrameAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw ApiFault.Invalid("Only UTF-8 JSON text frames are supported.");
            if (message.Length + result.Count > MaxFrameBytes) throw ApiFault.Invalid("Realtime frame exceeds 64 KiB.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<WireFrame>(message.ToArray(), Json) ?? throw new JsonException("Empty realtime frame.");
    }

    private static async Task SendFrameAsync(WebSocket socket, WireFrame frame, SemaphoreSlim sendGate, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (bytes.Length > MaxFrameBytes) throw new ApiFault(413, "frame_too_large", "Server response frame exceeds 64 KiB.");
        await sendGate.WaitAsync(token);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token); }
        finally { sendGate.Release(); }
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException("Frame payload is empty.");
    private static WireFrame Error(string code, string message, string sessionId) => new()
    {
        Kind = FrameKind.Error, SessionId = sessionId, UtcTimestamp = DateTimeOffset.UtcNow,
        PayloadJson = JsonSerializer.Serialize(new ErrorPayload { Code = code, Message = message }, Json)
    };
}
