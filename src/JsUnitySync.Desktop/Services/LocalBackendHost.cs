using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using JsUnitySync.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace JsUnitySync.Desktop.Services;

/// <summary>Starts the bundled loopback API when the configured local service is not already running.</summary>
public sealed class LocalBackendHost : IAsyncDisposable
{
    private readonly Uri _address;
    private readonly string? _databaseConnectionString;
    private readonly string? _assetRoot;
    private WebApplication? _ownedHost;

    public LocalBackendHost(Uri? address = null, string? databaseConnectionString = null, string? assetRoot = null)
    {
        _address = address ?? new Uri("http://localhost:5080/");
        _databaseConnectionString = databaseConnectionString;
        _assetRoot = assetRoot;
        if (_address.Scheme != Uri.UriSchemeHttp || !_address.IsLoopback || _address.AbsolutePath != "/")
            throw new ArgumentException("The bundled API may only bind an HTTP loopback origin.", nameof(address));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken)) return;

        // The packaged desktop can be launched without a separately installed .NET runtime.
        // SQLite and app-owned LocalAppData storage are the self-contained default.
        var arguments = new List<string>
        {
            "--environment", "Development",
            "--urls", _address.GetLeftPart(UriPartial.Authority),
            "--Database:Provider=Sqlite",
            "--Database:ApplyMigrationsOnStartup=true"
        };
        if (_databaseConnectionString is not null) arguments.Add($"--Database:ConnectionString={_databaseConnectionString}");
        if (_assetRoot is not null) arguments.Add($"--AssetSync:RootPath={_assetRoot}");
        var host = Program.CreateHost(arguments.ToArray(), TimeSpan.FromSeconds(5));
        try
        {
            await host.StartAsync(cancellationToken);
            _ownedHost = host;
            if (!await IsReadyAsync(cancellationToken))
                throw new InvalidOperationException("The local API started but did not become ready.");
        }
        catch
        {
            if (_ownedHost is not null)
            {
                await _ownedHost.StopAsync(CancellationToken.None);
                await _ownedHost.DisposeAsync();
                _ownedHost = null;
            }
            else await host.DisposeAsync();
            throw;
        }
    }

    public Uri ServerAddress => _address;

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(750) };
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(new Uri(_address, "health/ready"), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var result = await response.Content.ReadFromJsonAsync<Readiness>(cancellationToken);
                    if (result is { Service: "JsUnitySync.Api", Status: "ready" }) return true;
                }
                // A response indicates another service owns the port. Do not start a second host.
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
            if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedHost is null) return;
        var host = _ownedHost;
        _ownedHost = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await host.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally { await host.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed record Readiness(string? Status, string? Service);
}
