using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Contracts.Teams;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Realtime;
using JsUnitySync.Desktop.Services;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Domain.Scenes;
using JsUnitySync.Server.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JsUnitySync.Desktop.Tests;

public sealed class DesktopRealtimeClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<WireFrame> Receive(ClientWebSocket socket, CancellationToken token)
    {
        using var content = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Unexpected session close.");
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<WireFrame>(content.ToArray(), Json)!;
    }

    [Fact]
    public async Task DesktopClient_StoresIncomingEventEncryptedBeforeServerAck()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jsunitysync-desktop-realtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var databaseConnection = $"Data Source={Path.Combine(directory, "api.db")}; Foreign Keys=True";
        await using var api = JsUnitySync.Api.Program.CreateHost(["--urls", "http://127.0.0.1:0", "--environment", "Testing", $"--Database:ConnectionString={databaseConnection}"], TimeSpan.FromSeconds(2));
        AccountViewModel? account = null;
        ClientWebSocket? publisher = null;
        try
        {
            await api.StartAsync(deadline.Token);
            var address = new Uri(api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).AbsoluteUri;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
            var collaboration = new CollaborationClient(http); collaboration.SetServer(address);
            var sessionStore = new ShellViewModelTests.MemorySessionStore();
            account = new AccountViewModel(collaboration, sessionStore)
            { Email = "realtime@example.com", DisplayName = "Realtime Developer", RememberSession = true };
            await account.AuthenticateAsync("correct-horse-battery", register: true);
            Assert.True(account.IsSignedIn, account.Message);

            var team = await collaboration.SendAsync<TeamSummary>(HttpMethod.Post, "api/v1/teams", new CreateTeamRequest { Name = "Network Team" }, deadline.Token);
            var project = await collaboration.SendAsync<ProjectSummary>(HttpMethod.Post, $"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Network Game" }, deadline.Token);
            var session = await collaboration.SendAsync<SessionSummary>(HttpMethod.Post, $"api/v1/projects/{project.Id}/sessions", new CreateSessionRequest { Name = "WSS Test" }, deadline.Token);

            var path = Path.Combine(directory, "realtime-inbox.dpapi");
            var store = new ProtectedRealtimeClientStore(path);
            var cursor = store.GetOrCreateCursor(address, account.Profile!.Id, session.Id);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var client = new RealtimeSessionClient(collaboration, account, store);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var run = client.RunAsync(session.Id, status =>
            {
                statuses.Enqueue(status);
                if (status.StartsWith("Connected to session", StringComparison.Ordinal))
                {
                    connected.TrySetResult();
                    if (status.Contains("sequence 1", StringComparison.Ordinal)) reconnected.TrySetResult();
                }
            }, cancellation.Token);
            await connected.Task.WaitAsync(deadline.Token);

            publisher = new ClientWebSocket();
            publisher.Options.SetRequestHeader("Authorization", $"Bearer {sessionStore.Session!.Token}");
            var wsUri = new UriBuilder(new Uri(address)) { Scheme = "ws", Path = $"realtime/v1/sessions/{session.Id}" }.Uri;
            await publisher.ConnectAsync(wsUri, deadline.Token);
            var publisherClientId = Guid.NewGuid().ToString("N");
            var unitySceneGuid = Guid.NewGuid().ToString("N");
            var manifestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("unity test baseline"))).ToLowerInvariant();
            var hello = new WireFrame { Kind = FrameKind.Hello, SessionId = session.Id, UtcTimestamp = DateTimeOffset.UtcNow,
                PayloadJson = JsonSerializer.Serialize(new HelloPayload { UserId = account.Profile.Id, ClientId = publisherClientId, SceneGuid = unitySceneGuid, SceneManifestHash = manifestHash, LastAckedSequence = -1 }, Json) };
            await publisher.SendAsync(JsonSerializer.SerializeToUtf8Bytes(hello, Json), WebSocketMessageType.Text, true, deadline.Token);
            Assert.Equal(FrameKind.Welcome, (await Receive(publisher, deadline.Token)).Kind);

            const string privateValue = "private-scene-property-value";
            var eventId = StableId.NewId().ToString();
            var mutation = new SceneMutation { Kind = (int)SceneEventKind.PropertyChanged, ObjectId = StableId.NewId().ToString(), SceneGuid = unitySceneGuid,
                ComponentType = "Light", PropertyName = "intensity", BaseRevision = 0, SerializedValue = privateValue, DataJson = "{}" };
            var sceneEvent = new WireFrame { Kind = FrameKind.SceneEvent, SessionId = session.Id, FrameId = eventId,
                UtcTimestamp = DateTimeOffset.UtcNow, Sequence = -1, PayloadJson = JsonSerializer.Serialize(mutation, Json) };
            await publisher.SendAsync(JsonSerializer.SerializeToUtf8Bytes(sceneEvent, Json), WebSocketMessageType.Text, true, deadline.Token);
            var echo = await Receive(publisher, deadline.Token);
            Assert.Equal(eventId, echo.FrameId);
            for (var i = 0; i < 100 && store.GetSnapshot(address, account.Profile.Id, session.Id).LastAckedSequence < 1; i++)
                await Task.Delay(25, deadline.Token);

            var snapshot = store.GetSnapshot(address, account.Profile.Id, session.Id);
            Assert.Equal(1, snapshot.LastAckedSequence);
            Assert.Equal(eventId, Assert.Single(snapshot.RecentEvents).FrameId);
            Assert.Contains(statuses, text => text.StartsWith("Connected to session", StringComparison.Ordinal));
            var encrypted = await File.ReadAllBytesAsync(path, deadline.Token);
            Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(privateValue), encrypted);
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
            var remoteCursor = await db.SessionClientCursors.AsNoTracking().SingleAsync(x => x.SessionId == session.Id && x.ClientId == cursor.ClientId, deadline.Token);
            Assert.Equal(1, remoteCursor.LastAckedSequence);

            await publisher.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "publisher done", deadline.Token);
            publisher.Dispose(); publisher = null;
            await api.StopAsync(deadline.Token);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var restarted = JsUnitySync.Api.Program.CreateHost(["--urls", address, "--environment", "Testing", $"--Database:ConnectionString={databaseConnection}"], TimeSpan.FromSeconds(2));
            await restarted.StartAsync(deadline.Token);
            await reconnected.Task.WaitAsync(deadline.Token);
            Assert.Equal(1, store.GetSnapshot(address, account.Profile.Id, session.Id).LastAckedSequence);
            Assert.Single(store.GetSnapshot(address, account.Profile.Id, session.Id).RecentEvents);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally
        {
            if (publisher?.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await publisher.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeTimeout.Token); } catch (WebSocketException) { }
            }
            publisher?.Dispose();
            account?.Dispose();
            await api.StopAsync(deadline.Token);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await api.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

    }
}
