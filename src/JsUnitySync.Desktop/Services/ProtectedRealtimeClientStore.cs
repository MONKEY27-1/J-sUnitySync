using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Desktop.Core.Realtime;

namespace JsUnitySync.Desktop.Services;

/// <summary>CurrentUser-DPAPI protected event spool and resume cursors. Scene payloads are never written as plaintext.</summary>
public sealed class ProtectedRealtimeClientStore(string path) : IRealtimeClientStore
{
    private const int MaxStoredEvents = 20_000;
    private const int MaxStoredSessions = 1_000;
    private const int MaxEncryptedFileBytes = 128 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JsUnitySync.RealtimeInbox.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RealtimeCursor GetOrCreateCursor(string serverAddress, string userId, string sessionId)
    {
        lock (Gate)
        {
            var all = ReadAll();
            var state = Find(all, serverAddress, userId, sessionId);
            if (state is null)
            {
                if (all.Count >= MaxStoredSessions) throw new InvalidOperationException("Local realtime inbox session limit reached; export or remove old inbox data in a later version.");
                state = new LocalSession { ServerAddress = serverAddress, UserId = userId, SessionId = sessionId, ClientId = Guid.NewGuid().ToString("N") };
                all.Add(state);
                WriteAll(all);
            }
            return new RealtimeCursor(state.ClientId, state.LastAckedSequence);
        }
    }

    public RealtimeInboxSnapshot GetSnapshot(string serverAddress, string userId, string sessionId)
    {
        lock (Gate)
        {
            var state = Find(ReadAll(), serverAddress, userId, sessionId);
            return state is null
                ? new RealtimeInboxSnapshot(-1, 0, [], [])
                : new RealtimeInboxSnapshot(state.LastAckedSequence, state.AcceptedEvents.Count,
                    state.AcceptedEvents.TakeLast(50).ToArray(), state.Conflicts.TakeLast(20).ToArray());
        }
    }

    public Task<long> StoreAcceptedEventAsync(string serverAddress, string userId, string sessionId, string clientId, WireFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            var all = ReadAll();
            var state = Find(all, serverAddress, userId, sessionId) ?? throw new InvalidDataException("Realtime cursor is missing; reconnect to recreate it.");
            if (state.ClientId != clientId) throw new InvalidDataException("Realtime cursor belongs to a different client id.");
            if (frame.Kind != FrameKind.SceneEvent || frame.SessionId != sessionId || frame.Sequence <= state.LastAckedSequence)
            {
                if (frame.Kind == FrameKind.SceneEvent && frame.SessionId == sessionId && frame.Sequence <= state.LastAckedSequence)
                    return Task.FromResult(state.LastAckedSequence);
                throw new InvalidDataException("Realtime event does not match the session cursor.");
            }
            var expected = state.LastAckedSequence < 0 ? 1 : state.LastAckedSequence + 1;
            if (frame.Sequence != expected) throw new InvalidDataException("Realtime event sequence has a gap; the server will replay after reconnect.");
            if (state.AcceptedEvents.Count >= MaxStoredEvents) throw new IOException("Encrypted local realtime inbox is full; no acknowledgment was sent.");
            state.AcceptedEvents.Add(frame);
            state.LastAckedSequence = frame.Sequence;
            WriteAll(all); // Atomic DPAPI-protected replacement before the network ack.
            return Task.FromResult(state.LastAckedSequence);
        }
    }

    public Task StoreConflictAsync(string serverAddress, string userId, string sessionId, string clientId, WireFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            var all = ReadAll();
            var state = Find(all, serverAddress, userId, sessionId) ?? throw new InvalidDataException("Realtime cursor is missing.");
            if (state.ClientId != clientId || frame.Kind != FrameKind.Conflict || frame.SessionId != sessionId)
                throw new InvalidDataException("Realtime conflict does not match the connected session.");
            if (state.Conflicts.Any(x => x.FrameId == frame.FrameId)) return Task.CompletedTask;
            state.Conflicts.Add(frame);
            if (state.Conflicts.Count > 100) state.Conflicts.RemoveRange(0, state.Conflicts.Count - 100);
            WriteAll(all);
            return Task.CompletedTask;
        }
    }

    private static LocalSession? Find(List<LocalSession> all, string server, string user, string session) =>
        all.SingleOrDefault(x => x.ServerAddress == server && x.UserId == user && x.SessionId == session);

    private List<LocalSession> ReadAll()
    {
        if (!File.Exists(path)) return [];
        var encrypted = File.ReadAllBytes(path);
        if (encrypted.Length > MaxEncryptedFileBytes) throw new InvalidDataException("Encrypted realtime inbox exceeds the safe size limit.");
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var values = JsonSerializer.Deserialize<List<LocalSession>>(plain, Json)
                ?? throw new InvalidDataException("Realtime inbox is invalid; original file is preserved.");
            if (values.Count > MaxStoredSessions || values.Any(x => x.AcceptedEvents.Count > MaxStoredEvents || x.Conflicts.Count > 100))
                throw new InvalidDataException("Realtime inbox exceeds the safe entry limit; original file is preserved.");
            return values;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void WriteAll(List<LocalSession> values)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(values, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaxEncryptedFileBytes) throw new IOException("Encrypted realtime inbox reached its safe file-size limit; no event was acknowledged.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class LocalSession
    {
        public string ServerAddress { get; set; } = "";
        public string UserId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public long LastAckedSequence { get; set; } = -1;
        public List<WireFrame> AcceptedEvents { get; set; } = [];
        public List<WireFrame> Conflicts { get; set; } = [];
    }
}
