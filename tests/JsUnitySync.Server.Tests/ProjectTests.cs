using System.Net;
using System.Net.Http.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Contracts.Teams;

namespace JsUnitySync.Server.Tests;

public sealed class ProjectTests
{
    private static async Task<AuthResponse> User(ApiFixture f, string email)
    {
        var r = await f.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = email[..email.IndexOf('@')], Password = "correct-horse-battery" });
        r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
    private static void As(ApiFixture f, AuthResponse user) => f.Authenticate(user.Token);
    private static async Task<TeamSummary> Team(ApiFixture f)
    {
        var r = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Project Team" });
        r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<TeamSummary>())!;
    }

    [Fact]
    public async Task Projects_AuthorizedCreateReadUpdateAndArchiveAreVersioned()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "owner@example.com"); var member = await User(f, "member@example.com");
        As(f, owner); var team = await Team(f);
        var invitationResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = member.Profile.Id });
        var invitation = (await invitationResponse.Content.ReadFromJsonAsync<TeamInvitationSummary>())!;
        As(f, member);
        await f.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/accept", new InvitationResponseRequest { ExpectedRevision = invitation.Revision });
        As(f, owner);

        var createdResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "  Arena  " });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var project = (await createdResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        Assert.Equal("Arena", project.Name); Assert.Equal(team.Id, project.TeamId); Assert.Equal("Project Team", project.TeamName); Assert.Equal(1, project.Revision);
        var stale = await f.Client.PutAsJsonAsync($"api/v1/teams/{team.Id}/projects/{project.Id}", new UpdateProjectRequest { Name = "Stale", ExpectedRevision = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var update = await f.Client.PutAsJsonAsync($"api/v1/teams/{team.Id}/projects/{project.Id}", new UpdateProjectRequest { Name = "Arena Remastered", ExpectedRevision = 1 });
        project = (await update.Content.ReadFromJsonAsync<ProjectSummary>())!;
        Assert.Equal("Arena Remastered", project.Name); Assert.Equal(2, project.Revision);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects/{project.Id}/archive", new ArchiveProjectRequest { ExpectedRevision = 2 })).StatusCode);
        Assert.Empty((await f.Client.GetFromJsonAsync<ProjectSummary[]>($"api/v1/teams/{team.Id}/projects"))!);
        var archived = Assert.Single((await f.Client.GetFromJsonAsync<ProjectSummary[]>($"api/v1/teams/{team.Id}/projects?includeArchived=true"))!);
        Assert.True(archived.IsArchived); Assert.Equal(3, archived.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects/{project.Id}/archive", new ArchiveProjectRequest { ExpectedRevision = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects/{project.Id}/restore", new ArchiveProjectRequest { ExpectedRevision = 3 })).StatusCode);
        var restored = Assert.Single((await f.Client.GetFromJsonAsync<ProjectSummary[]>($"api/v1/teams/{team.Id}/projects"))!);
        Assert.False(restored.IsArchived); Assert.Equal(4, restored.Revision);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects/{project.Id}/archive", new ArchiveProjectRequest { ExpectedRevision = 4 })).StatusCode);

        As(f, member);
        var memberList = await f.Client.GetFromJsonAsync<ProjectSummary[]>($"api/v1/teams/{team.Id}/projects?includeArchived=true");
        Assert.True(Assert.Single(memberList!).IsArchived);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Unauthorized" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync("api/v1/teams/01ARZ3NDEKTSV4RRFFQ69G5FAV/projects")).StatusCode);
    }

    [Fact]
    public async Task Projects_RejectDuplicateNamesAndInvalidNames()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "owner@example.com"); As(f, owner); var team = await Team(f);
        Assert.Equal(HttpStatusCode.Created, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Game" })).StatusCode);
        var duplicate = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "game" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var invalid = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = " " });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
