using JsUnitySync.Contracts.Protocol;

namespace JsUnitySync.Desktop.Core.Realtime;

public sealed record RealtimeCursor(string ClientId, long LastAckedSequence);
public sealed record RealtimeInboxSnapshot(long LastAckedSequence, int AcceptedEventCount, IReadOnlyList<WireFrame> RecentEvents, IReadOnlyList<WireFrame> RecentConflicts);

/// <summary>Must durably store an accepted event before its sequence is acknowledged to the server.</summary>
public interface IRealtimeClientStore
{
    RealtimeCursor GetOrCreateCursor(string serverAddress, string userId, string sessionId);
    RealtimeInboxSnapshot GetSnapshot(string serverAddress, string userId, string sessionId);
    Task<long> StoreAcceptedEventAsync(string serverAddress, string userId, string sessionId, string clientId, WireFrame frame, CancellationToken cancellationToken);
    Task StoreConflictAsync(string serverAddress, string userId, string sessionId, string clientId, WireFrame frame, CancellationToken cancellationToken);
}
