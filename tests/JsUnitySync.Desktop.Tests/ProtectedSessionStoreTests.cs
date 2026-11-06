using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Realtime;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Desktop.Services;

namespace JsUnitySync.Desktop.Tests;

public sealed class ProtectedSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"JsUnitySync-dpapi-tests-{Guid.NewGuid():N}");
    private string PathName => Path.Combine(_directory, "session.dpapi");

    [Fact]
    public void Store_ProtectsAndRestoresAccountSessionForCurrentWindowsUser()
    {
        var store = new ProtectedSessionStore(PathName);
        var session = new StoredSession("https://sync.example.com/", new string('A', 64), DateTimeOffset.UtcNow.AddHours(2));
        store.Save(session);
        var bytes = File.ReadAllBytes(PathName);
        Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(session.Token), bytes);
        Assert.Equal(session, store.Load());
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Store_CorruptionIsReportedAndDoesNotSilentlyOverwrite()
    {
        Directory.CreateDirectory(_directory);
        byte[] corrupted = [1, 2, 3, 4, 5];
        File.WriteAllBytes(PathName, corrupted);
        var store = new ProtectedSessionStore(PathName);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => store.Load());
        Assert.Equal(corrupted, File.ReadAllBytes(PathName));
    }

    [Fact]
    public async Task RealtimeStore_EncryptsEventPayloadBeforePersistingResumeCursor()
    {
        var store = new ProtectedRealtimeClientStore(Path.Combine(_directory, "realtime.dpapi"));
        const string server = "https://sync.example.com/";
        const string user = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        const string session = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var cursor = store.GetOrCreateCursor(server, user, session);
        Assert.Equal(-1, cursor.LastAckedSequence);
        var secret = "serialized-private-scene-property";
        await store.StoreAcceptedEventAsync(server, user, session, cursor.ClientId, new WireFrame
        {
            Kind = FrameKind.SceneEvent, FrameId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", SessionId = session,
            Sequence = 1, PayloadJson = secret, UtcTimestamp = DateTimeOffset.UtcNow
        }, default);
        var encrypted = File.ReadAllBytes(Path.Combine(_directory, "realtime.dpapi"));
        Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(secret), encrypted);
        var restored = store.GetOrCreateCursor(server, user, session);
        Assert.Equal(cursor.ClientId, restored.ClientId);
        Assert.Equal(1, restored.LastAckedSequence);
        Assert.Equal(secret, Assert.Single(store.GetSnapshot(server, user, session).RecentEvents).PayloadJson);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
