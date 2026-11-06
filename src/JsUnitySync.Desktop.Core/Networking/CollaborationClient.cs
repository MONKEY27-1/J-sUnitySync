using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Settings;

namespace JsUnitySync.Desktop.Core.Networking;

public sealed class ApiClientException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>No automatic mutation retries: on ambiguous transport failures users must refresh before retrying.</summary>
public sealed class CollaborationClient(HttpClient http)
{
    private Uri _server = new(new DesktopSettings().ServerAddress);
    private string? _token;
    public string ServerAddress => _server.AbsoluteUri;
    internal string? RealtimeAccessToken => _token;
    public event EventHandler? SessionInvalidated;
    public void SetServer(string address)
    {
        if (!ServerEndpoint.TryParse(address, out var server)) throw new ArgumentException(ServerEndpoint.ValidationMessage);
        if (_server != server) { _server = server; ClearSession(); }
    }
    public void SetSession(StoredSession session)
    {
        if (session.ServerAddress != ServerAddress || session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Session does not belong to this server or is expired.");
        _token = session.Token;
    }
    public void ClearSession() => _token = null;

    private static bool IsSafeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Contains('#') || route.Contains('\\') || route.Contains("..", StringComparison.Ordinal)) return false;
        var queryIndex = route.IndexOf('?');
        var path = queryIndex < 0 ? route : route[..queryIndex];
        if (!path.StartsWith("api/v1/", StringComparison.Ordinal) || !Uri.TryCreate(path, UriKind.Relative, out _)) return false;
        if (queryIndex < 0) return true;
        var query = route[(queryIndex + 1)..];
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0] is "includeArchived=true" or "includeArchived=false") return true;
        if (parts.Length != 1 || !parts[0].StartsWith("path=", StringComparison.Ordinal)) return false;
        try
        {
            var value = Uri.UnescapeDataString(parts[0][5..]);
            return value.StartsWith("Assets/", StringComparison.Ordinal) && !value.StartsWith('/') && !value.Contains('\\') &&
                !value.Contains(':') && !value.Split('/').Any(segment => segment is "" or "." or "..") &&
                value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !value.Any(char.IsControl);
        }
        catch (UriFormatException) { return false; }
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken token, bool authenticated = true)
    {
        if (!IsSafeRoute(route))
            throw new ArgumentException("Only versioned relative API routes and allow-listed query parameters are permitted.", nameof(route));
        var origin = _server;
        using var request = new HttpRequestMessage(method, new Uri(origin, route));
        if (authenticated)
        {
            if (_token is null) throw new ApiClientException(401, "Sign in to continue.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            // A response from a previously selected origin cannot update the new workspace.
            if (origin != _server) throw new OperationCanceledException("Server changed.", token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated)
                {
                    ClearSession();
                    SessionInvalidated?.Invoke(this, EventArgs.Empty);
                }
                ApiError? error = null;
                try { error = await response.Content.ReadFromJsonAsync<ApiError>(timeout.Token); }
                catch (Exception e) when (e is JsonException or NotSupportedException) { }
                throw new ApiClientException((int)response.StatusCode, error?.Message ?? $"Server returned HTTP {(int)response.StatusCode}. Refresh before retrying a write.");
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return default!;
            return await response.Content.ReadFromJsonAsync<T>(timeout.Token)
                ?? throw new ApiClientException(502, "Server returned an empty response.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ApiClientException(408, "Request timed out. A write may have completed; refresh before retrying."); }
        catch (HttpRequestException)
        { throw new ApiClientException(503, "Server unavailable. A write may have completed; refresh before retrying."); }
        catch (JsonException)
        { throw new ApiClientException(502, "Server returned an invalid response. Refresh before retrying a write."); }
    }
}
