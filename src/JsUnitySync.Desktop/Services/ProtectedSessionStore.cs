using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsUnitySync.Desktop.Core.Accounts;

namespace JsUnitySync.Desktop.Services;

public sealed class ProtectedSessionStore(string path) : ISessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JsUnitySync.Session.v1");
    public StoredSession? Load()
    {
        if (!File.Exists(path)) return null;
        var encrypted = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<StoredSession>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(StoredSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete() => File.Delete(path);
}
