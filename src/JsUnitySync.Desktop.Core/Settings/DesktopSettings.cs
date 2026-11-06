namespace JsUnitySync.Desktop.Core.Settings;

public sealed record DesktopSettings(string ServerAddress = "http://localhost:5080/", bool ReduceMotion = false);

public static class ServerEndpoint
{
    public static bool TryParse(string? address, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            return false;
        endpoint = uri;
        return true;
    }

    public const string ValidationMessage = "Enter a server origin such as https://sync.example.com or http://localhost:5080. HTTP is allowed only for loopback; paths, credentials and query strings are not allowed.";
}
