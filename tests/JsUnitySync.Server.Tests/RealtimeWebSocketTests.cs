using System.Net.WebSockets;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Contracts.Teams;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Domain.Scenes;
using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JsUnitySync.Server.Tests;

public sealed class RealtimeWebSocketTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<AuthResponse> Register(ApiFixture fixture, string email)
    {
        var response = await fixture.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = email[..email.IndexOf('@')], Password = "correct-horse-battery" });
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<SessionSummary> CreateSession(ApiFixture f, AuthResponse user)
    {
        f.Authenticate(user.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Realtime Team" });
        teamResponse.EnsureSuccessStatusCode();
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Realtime Project" });
        projectResponse.EnsureSuccessStatusCode();
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        var sessionResponse = await f.Client.PostAsJsonAsync($"api/v1/projects/{project.Id}/sessions", new CreateSessionRequest { Name = "Shared Scene" });
        sessionResponse.EnsureSuccessStatusCode();
        return (await sessionResponse.Content.ReadFromJsonAsync<SessionSummary>())!;
    }

    private static async Task<ClientWebSocket> Join(ApiFixture f, SessionSummary session, AuthResponse user, string clientId, long lastAck, Action<SessionWelcomePayload>? onWelcome = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {user.Token}");
        var address = new UriBuilder(f.Client.BaseAddress!) { Scheme = "ws", Path = $"realtime/v1/sessions/{session.Id}" }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(address, timeout.Token);
        var requestedSceneGuid = string.IsNullOrEmpty(session.SceneGuid) ? Guid.NewGuid().ToString("N") : session.SceneGuid;
        var requestedManifest = string.IsNullOrEmpty(session.SceneManifestHash)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("shared baseline"))).ToLowerInvariant()
            : session.SceneManifestHash;
        var hello = new WireFrame
        {
            Kind = FrameKind.Hello, SessionId = session.Id, UtcTimestamp = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new HelloPayload { UserId = user.Profile.Id, ClientId = clientId, SceneGuid = requestedSceneGuid, SceneManifestHash = requestedManifest, LastAckedSequence = lastAck }, Json)
        };
        await Send(socket, hello, timeout.Token);
        var welcome = await Receive(socket, timeout.Token);
        Assert.True(welcome.Kind == FrameKind.Welcome, $"Expected Welcome, received {welcome.Kind}: {welcome.PayloadJson}");
        var welcomePayload = JsonSerializer.Deserialize<SessionWelcomePayload>(welcome.PayloadJson, Json)!;
        if (!string.IsNullOrEmpty(welcomePayload.SceneGuid)) { session.SceneGuid = welcomePayload.SceneGuid; session.SceneManifestHash = welcomePayload.SceneManifestHash; }
        onWelcome?.Invoke(welcomePayload);
        return socket;
    }

    private static Task Send(ClientWebSocket socket, WireFrame frame, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, Json), WebSocketMessageType.Text, true, token);

    private static async Task<WireFrame> Receive(ClientWebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(bytes, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Peer closed before the expected frame.");
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            Assert.True(data.Length + result.Count <= 64 * 1024);
            data.Write(bytes, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<WireFrame>(data.ToArray(), Json)!;
    }

    private static WireFrame Event(string sessionId, string eventId, SceneMutation mutation) => new()
    {
        Kind = FrameKind.SceneEvent, FrameId = eventId, SessionId = sessionId, Sequence = -1,
        UtcTimestamp = DateTimeOffset.UtcNow, PayloadJson = JsonSerializer.Serialize(mutation, Json)
    };
    private static SceneMutation Property(string sceneGuid, string objectId, long revision, string value) => new()
    {
        Kind = (int)SceneEventKind.PropertyChanged, ObjectId = objectId, SceneGuid = sceneGuid, ComponentType = "Light",
        PropertyName = "m_Intensity", BaseRevision = revision, SerializedValue = value, DataJson = "{}"
    };
    private static WireFrame Ack(string sessionId, long sequence) => new()
    {
        Kind = FrameKind.EventAck, SessionId = sessionId, UtcTimestamp = DateTimeOffset.UtcNow,
        PayloadJson = JsonSerializer.Serialize(new EventAckPayload { HighestAppliedSequence = sequence }, Json)
    };

    [Fact]
    public async Task Realtime_TwoPeersReceiveDurableOrderedEventsAndReconnectReplaysUnackedWork()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var owner = await Register(fixture, "scene-owner@example.com");
        var session = await CreateSession(fixture, owner);
        var teams = await fixture.Client.GetFromJsonAsync<TeamSummary[]>("api/v1/me/teams");
        var team = Assert.Single(teams!);
        var collaborator = await Register(fixture, "scene-collaborator@example.com");
        fixture.Authenticate(owner.Token);
        var invitation = (await (await fixture.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = collaborator.Profile.Id })).Content.ReadFromJsonAsync<TeamInvitationSummary>())!;
        fixture.Authenticate(collaborator.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/accept", new InvitationResponseRequest { ExpectedRevision = invitation.Revision })).StatusCode);
        var clientAId = Guid.NewGuid().ToString("N");
        var clientBId = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var a = await Join(fixture, session, owner, clientAId, -1);
        using var b = await Join(fixture, session, collaborator, clientBId, -1);
        using (var wrongScene = new ClientWebSocket())
        {
            wrongScene.Options.SetRequestHeader("Authorization", $"Bearer {owner.Token}");
            var address = new UriBuilder(fixture.Client.BaseAddress!) { Scheme = "ws", Path = $"realtime/v1/sessions/{session.Id}" }.Uri;
            await wrongScene.ConnectAsync(address, timeout.Token);
            await Send(wrongScene, new WireFrame { Kind = FrameKind.Hello, SessionId = session.Id, UtcTimestamp = DateTimeOffset.UtcNow,
                PayloadJson = JsonSerializer.Serialize(new HelloPayload { UserId = owner.Profile.Id, ClientId = Guid.NewGuid().ToString("N"),
                    SceneGuid = session.SceneGuid, SceneManifestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("wrong baseline"))).ToLowerInvariant(), LastAckedSequence = -1 }, Json) }, timeout.Token);
            var sceneError = await Receive(wrongScene, timeout.Token);
            Assert.Equal(FrameKind.Error, sceneError.Kind);
            Assert.Equal("scene_mismatch", JsonSerializer.Deserialize<ErrorPayload>(sceneError.PayloadJson, Json)!.Code);
        }

        var objectId = StableId.NewId().ToString();
        var rename = Event(session.Id, StableId.NewId().ToString(), new SceneMutation
        { Kind = (int)SceneEventKind.GameObjectRenamed, ObjectId = objectId, SceneGuid = session.SceneGuid, BaseRevision = 0, SerializedValue = "Directional Light", DataJson = "{\"newName\":\"Directional Light\"}" });
        await Send(a, rename, timeout.Token);
        var eventA = await Receive(a, timeout.Token);
        var eventB = await Receive(b, timeout.Token);
        Assert.Equal(1, eventA.Sequence); Assert.Equal(1, eventB.Sequence);
        Assert.Equal(rename.FrameId, eventA.FrameId); Assert.Equal(eventA.FrameId, eventB.FrameId);
        Assert.Equal(owner.Profile.Id, eventA.AuthorId);
        await Send(a, Ack(session.Id, 1), timeout.Token);
        await Send(b, Ack(session.Id, 1), timeout.Token);
        await Send(a, rename, timeout.Token); // Duplicate event id is acknowledged idempotently; sequence remains 1.

        var propertyEventId = StableId.NewId().ToString();
        await Send(a, Event(session.Id, propertyEventId, Property(session.SceneGuid, objectId, 0, "2.0")), timeout.Token);
        var acceptedA = await Receive(a, timeout.Token);
        var unackedB = await Receive(b, timeout.Token);
        Assert.Equal(2, acceptedA.Sequence); Assert.Equal(propertyEventId, acceptedA.FrameId);
        Assert.Equal(acceptedA.FrameId, unackedB.FrameId);
        await Send(a, Ack(session.Id, 2), timeout.Token);
        await b.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "simulate disconnect before durable ack", timeout.Token);
        b.Dispose();

        SessionWelcomePayload? welcome = null;
        using var resumed = await Join(fixture, session, collaborator, clientBId, 1, payload => welcome = payload);
        Assert.Equal(2, welcome!.LastSequence); Assert.Equal(1, welcome.ReplayFromSequence);
        var replayed = await Receive(resumed, timeout.Token);
        Assert.Equal(propertyEventId, replayed.FrameId); Assert.Equal(2, replayed.Sequence);
        await Send(resumed, Ack(session.Id, 2), timeout.Token);

        var rejectedEventId = StableId.NewId().ToString();
        await Send(resumed, Event(session.Id, rejectedEventId, Property(session.SceneGuid, objectId, 0, "9.0")), timeout.Token);
        var conflict = await Receive(resumed, timeout.Token);
        var conflictForAcceptedPeer = await Receive(a, timeout.Token);
        Assert.Equal(FrameKind.Conflict, conflict.Kind);
        Assert.Equal(conflict.FrameId, conflictForAcceptedPeer.FrameId);
        var details = JsonSerializer.Deserialize<SceneConflictPayload>(conflict.PayloadJson, Json)!;
        Assert.Equal(1, details.AcceptedRevision); Assert.Equal("2.0", details.AcceptedValue); Assert.Equal("9.0", details.RejectedValue);
        Assert.Equal(rejectedEventId, details.RejectedEventId);
        Assert.Equal(owner.Profile.Id, details.AcceptedById);
        Assert.Equal(collaborator.Profile.Id, details.RejectedById);
        Assert.Equal(collaborator.Profile.Id, conflict.AuthorId);

        using var laterClient = await Join(fixture, session, owner, Guid.NewGuid().ToString("N"), 2);
        var replayedConflict = await Receive(laterClient, timeout.Token);
        Assert.Equal(FrameKind.Conflict, replayedConflict.Kind);
        Assert.Equal(conflict.FrameId, replayedConflict.FrameId);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
        Assert.Equal(2, await db.SessionEvents.CountAsync(x => x.SessionId == session.Id));
        Assert.Equal(2, await db.CollaborationSessions.Where(x => x.Id == session.Id).Select(x => x.LastSequence).SingleAsync());
        Assert.Single(await db.SessionConflicts.Where(x => x.SessionId == session.Id).ToArrayAsync());
    }

    [Fact]
    public async Task Realtime_SessionMembershipAndClientFramesAreAuthorized()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var owner = await Register(fixture, "owner@example.com");
        var outsider = await Register(fixture, "outsider@example.com");
        var session = await CreateSession(fixture, owner);
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var denied = new ClientWebSocket();
        denied.Options.SetRequestHeader("Authorization", $"Bearer {outsider.Token}");
        var address = new UriBuilder(fixture.Client.BaseAddress!) { Scheme = "ws", Path = $"realtime/v1/sessions/{session.Id}" }.Uri;
        await Assert.ThrowsAnyAsync<WebSocketException>(() => denied.ConnectAsync(address, token.Token));
        using var anonymous = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => anonymous.ConnectAsync(address, token.Token));
    }
}
