using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using JsUnitySync.Desktop.Services;

namespace JsUnitySync.Desktop.Tests;

public sealed class LocalBackendHostTests
{
    [Fact]
    public async Task LocalBackendHost_StartsMigrationsAndStopsOnlyItsOwnedLoopbackApi()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jsunitysync-local-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}/");
        var database = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "local.db"), ForeignKeys = true }.ToString();
        var api = new LocalBackendHost(address, database, Path.Combine(directory, "assets"));
        try
        {
            await api.StartAsync();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var ready = await http.GetFromJsonAsync<Readiness>(new Uri(address, "health/ready"));
            Assert.Equal("ready", ready!.Status);
            Assert.Equal("Sqlite", ready.Database);
            Assert.True(File.Exists(Path.Combine(directory, "local.db")));
            await api.DisposeAsync();
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(new Uri(address, "health/live")));
        }
        finally
        {
            await api.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed record Readiness(string? Status, string? Database);
}
