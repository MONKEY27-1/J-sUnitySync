namespace JsUnitySync.Desktop.Core.Accounts;

public sealed record StoredSession(string ServerAddress, string Token, DateTimeOffset ExpiresAt);

/// <summary>Implementations must protect bearer tokens using the OS credential boundary, not plaintext settings.</summary>
public interface ISessionStore
{
    StoredSession? Load();
    void Save(StoredSession session);
    void Delete();
}
