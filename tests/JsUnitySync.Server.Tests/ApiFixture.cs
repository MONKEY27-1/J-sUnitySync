using System.Net;
using JsUnitySync.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JsUnitySync.Server.Tests;

internal sealed class ApiFixture : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"jsunitysync-api-tests-{Guid.NewGuid():N}");
    private IHost? _host;
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Fixture not started.");

    public async Task Start()
    {
        Directory.CreateDirectory(_directory);
        var database = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "test.db"), ForeignKeys = true, DefaultTimeout = 5
        }.ToString();
        _host = Program.CreateHost(["--urls", "http://127.0.0.1:0", "--environment", "Testing", $"--Database:ConnectionString={database}", $"--AssetSync:RootPath={Path.Combine(_directory, "assets")}"]);
        await _host.StartAsync();
        var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(addresses.Addresses.Single()) };
    }

    public void Authenticate(string? token) => Client.DefaultRequestHeaders.Authorization =
        token is null ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Delete(_directory, true); break; }
                catch (IOException) when (attempt < 4) { await Task.Delay(100); }
            }
        }
    }
}
