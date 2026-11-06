using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Editor
{
    internal sealed class UnitySceneTransport : IDisposable
    {
        private const int MaxFrameBytes = 64 * 1024;
        private readonly UnityApiClient _api;
        private readonly UnityInboxState _identity;
        private readonly UnityEncryptedInbox _inbox;
        private readonly ConcurrentQueue<UnityWireFrame> _outgoing = new ConcurrentQueue<UnityWireFrame>();
        private readonly SemaphoreSlim _outgoingSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _run = Task.CompletedTask;
        private string _sessionId = string.Empty;
        private string _sceneGuid = string.Empty;
        private string _sceneManifestHash = string.Empty;
        private bool _disposed;

        public event Action<UnityWireFrame> AcceptedEvent;
        public event Action<UnityWireFrame> Conflict;
        public event Action<string> Status;
        public bool IsRunning { get { return !_run.IsCompleted; } }
        public string UserId { get { return _api.UserId; } }

        public UnitySceneTransport(UnityApiClient api, UnityEncryptedInbox inbox, string sessionId, string sceneGuid, string sceneManifestHash)
        {
            _api = api; _inbox = inbox; _identity = inbox.Identity; _sessionId = sessionId; _sceneGuid = sceneGuid; _sceneManifestHash = sceneManifestHash;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnitySceneTransport));
            if (IsRunning) return;
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            _run = RunAsync(_lifetime.Token);
        }

        public void Stop() { if (!_disposed && !_lifetime.IsCancellationRequested) _lifetime.Cancel(); }

        public bool QueueMutation(UnitySceneMutation mutation)
        {
            if (_disposed || !StableIdFactory.IsValid(mutation.objectId) || mutation.sceneGuid != _sceneGuid) return false;
            var frame = new UnityWireFrame
            {
                kind = (int)UnityFrameKind.SceneEvent,
                frameId = StableIdFactory.NewId(),
                sessionId = _sessionId,
                sequence = -1,
                payloadJson = JsonUtility.ToJson(mutation)
            };
            _inbox.QueueOutgoing(frame);
            _outgoing.Enqueue(frame);
            _outgoingSignal.Release();
            return true;
        }

        private async Task RunAsync(CancellationToken token)
        {
            var retries = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ConnectOnceAsync(token).ConfigureAwait(false);
                    throw new WebSocketException("Session socket closed.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is WebSocketException || error is HttpRequestException || error is IOException)
                {
                    retries++;
                    if (retries > 5) { Status?.Invoke("Session connection failed after five attempts. Retry from the J'sUnitySync window."); return; }
                    var delay = UnityRetryDelay(retries);
                    Status?.Invoke("Session interrupted (" + error.GetType().Name + "); durable events are preserved. Reconnecting in " + delay.TotalSeconds.ToString("0.0") + " seconds.");
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    var detail = error is InvalidDataException ? error.Message : error.GetType().Name;
                    Status?.Invoke("Session stopped safely: " + detail + ". Reconnect after resolving the Editor/session state.");
                    return;
                }
            }
        }

        private async Task ConnectOnceAsync(CancellationToken token)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _api.AccessToken);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var origin = new Uri(_api.ServerAddress);
            var address = new UriBuilder(origin)
            {
                Scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Path = "realtime/v1/sessions/" + Uri.EscapeDataString(_sessionId),
                Query = "", Fragment = ""
            }.Uri;
            try
            {
                await socket.ConnectAsync(address, token).ConfigureAwait(false);
                var hello = new UnityWireFrame
                {
                    kind = (int)UnityFrameKind.Hello, sessionId = _sessionId,
                    payloadJson = JsonUtility.ToJson(new UnityHelloPayload
                    {
                        userId = _api.UserId, clientId = _identity.clientId, sceneGuid = _sceneGuid, sceneManifestHash = _sceneManifestHash,
                        protocolVersion = "2.0", lastAckedSequence = _identity.lastAckedSequence
                    })
                };
                await SendFrameAsync(socket, hello, token).ConfigureAwait(false);
                var welcome = await ReceiveFrameAsync(socket, token).ConfigureAwait(false);
                if (welcome.kind != (int)UnityFrameKind.Welcome)
                {
                    var rejection = welcome.kind == (int)UnityFrameKind.Error ? JsonUtility.FromJson<UnityErrorPayload>(welcome.payloadJson) : null;
                    throw new InvalidDataException(rejection == null ? "Collaboration server rejected the Unity session (frame kind " + welcome.kind + ")." : rejection.code + ": " + rejection.message);
                }
                var info = JsonUtility.FromJson<UnityWelcomePayload>(welcome.payloadJson);
                if (info == null || info.protocolVersion != "2.0" || info.sceneGuid != _sceneGuid || info.sceneManifestHash != _sceneManifestHash)
                    throw new InvalidDataException("This session is bound to a different Unity Scene or protocol version.");
                Status?.Invoke("Connected to the central event log. Replaying " + Math.Max(0, info.lastSequence - _identity.lastAckedSequence) + " accepted event(s).");

                foreach (var pending in _inbox.PendingOutgoing())
                    await SendFrameAsync(socket, pending, token).ConfigureAwait(false);

                using (var connection = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var sender = SendLoopAsync(socket, connection.Token);
                    var receiver = ReceiveLoopAsync(socket, connection.Token);
                    await Task.WhenAny(sender, receiver).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(sender, receiver).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { }
                }
            }
            finally { socket.Dispose(); }
        }

        private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await _outgoingSignal.WaitAsync(token).ConfigureAwait(false);
                while (_outgoing.TryDequeue(out var frame))
                    await SendFrameAsync(socket, frame, token).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var frame = await ReceiveFrameAsync(socket, token).ConfigureAwait(false);
                if (frame.kind == (int)UnityFrameKind.SceneEvent)
                {
                    var appliedSequence = _inbox.Accept(frame);
                    AcceptedEvent?.Invoke(frame); // Editor main-thread dispatcher applies the durable event.
                    var ack = new UnityWireFrame
                    {
                        kind = (int)UnityFrameKind.EventAck, sessionId = _sessionId,
                        payloadJson = JsonUtility.ToJson(new UnityEventAckPayload { highestAppliedSequence = appliedSequence })
                    };
                    await SendFrameAsync(socket, ack, token).ConfigureAwait(false);
                }
                else if (frame.kind == (int)UnityFrameKind.Conflict)
                {
                    _inbox.StoreConflict(frame);
                    Conflict?.Invoke(frame);
                }
                else if (frame.kind == (int)UnityFrameKind.Error)
                {
                    var error = JsonUtility.FromJson<UnityErrorPayload>(frame.payloadJson);
                    throw new InvalidDataException(error == null ? "Realtime server returned an error." : error.message);
                }
            }
            throw new WebSocketException("Realtime connection closed.");
        }

        private async Task SendFrameAsync(ClientWebSocket socket, UnityWireFrame frame, CancellationToken token)
        {
            if (string.IsNullOrEmpty(frame.utcTimestamp)) frame.utcTimestamp = DateTime.UtcNow.ToString("o");
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(frame));
            if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Realtime frame exceeds 64 KiB.");
            await _sendGate.WaitAsync(token).ConfigureAwait(false);
            try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
            finally { _sendGate.Release(); }
        }

        private static async Task<UnityWireFrame> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            using (var content = new MemoryStream())
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Realtime server closed the session.");
                    if (result.MessageType != WebSocketMessageType.Text || content.Length + result.Count > MaxFrameBytes)
                        throw new InvalidDataException("Realtime frame type or size is invalid.");
                    content.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }
                var frame = JsonUtility.FromJson<UnityWireFrame>(Encoding.UTF8.GetString(content.ToArray()));
                return frame ?? throw new InvalidDataException("Realtime frame was empty.");
            }
        }

        private static TimeSpan UnityRetryDelay(int attempt)
        {
            var cap = Math.Min(8000, 250 * Math.Pow(2, Math.Max(0, attempt - 1)));
            return TimeSpan.FromMilliseconds(UnityEngine.Random.Range(50f, (float)Math.Max(51, cap)));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _lifetime.Cancel();
            _disposed = true;
            // Do not dispose semaphores while async websocket tasks may still use them.
            _lifetime.Dispose();
        }
    }
}
