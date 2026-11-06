using System.Net;
using System.Net.Http.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Contracts.Scripting;
using JsUnitySync.Contracts.Teams;

namespace JsUnitySync.Server.Tests;

public sealed class ScriptDocumentTests
{
    private static async Task<AuthResponse> User(ApiFixture f, string email)
    {
        var response = await f.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = "Script User", Password = "correct-horse-battery" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task ScriptDocuments_AuthorizedCollaboratorsShareRevisionCheckedContent()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "scripts-owner@example.com"); f.Authenticate(owner.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Script Team" });
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Script Project" });
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        const string path = "Assets/Scripts/Ship.cs";

        var create = await f.Client.PutAsJsonAsync($"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(path)}", new SaveScriptDocumentRequest { ExpectedRevision = 0, Content = "public class Ship {}" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var initial = (await create.Content.ReadFromJsonAsync<ScriptDocumentSummary>())!;
        Assert.Equal(1, initial.Revision);
        Assert.Equal("public class Ship {}", initial.Content);

        var stale = await f.Client.PutAsJsonAsync($"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(path)}", new SaveScriptDocumentRequest { ExpectedRevision = 0, Content = "public class Ship { }" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var saved = await f.Client.PutAsJsonAsync($"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(path)}", new SaveScriptDocumentRequest { ExpectedRevision = initial.Revision, Content = "public class Ship { public int Hull; }" });
        var latest = (await saved.Content.ReadFromJsonAsync<ScriptDocumentSummary>())!;
        Assert.Equal(2, latest.Revision);
        Assert.Contains("Hull", latest.Content);

        var list = await f.Client.GetFromJsonAsync<ScriptDocumentSummary[]>($"api/v1/projects/{project.Id}/scripts");
        var listed = Assert.Single(list!);
        Assert.Equal(path, listed.Path);
        Assert.Equal(2, listed.Revision);
        Assert.Equal("", listed.Content);

        const string largePath = "Assets/Scripts/Large.cs";
        var largeText = new string('x', 100_000);
        var largeSave = await f.Client.PutAsJsonAsync($"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(largePath)}", new SaveScriptDocumentRequest { ExpectedRevision = 0, Content = largeText });
        Assert.Equal(HttpStatusCode.OK, largeSave.StatusCode);
        Assert.Equal(largeText, (await largeSave.Content.ReadFromJsonAsync<ScriptDocumentSummary>())!.Content);

        f.Authenticate(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync($"api/v1/projects/{project.Id}/scripts")).StatusCode);
        var outsider = await User(f, "scripts-outsider@example.com"); f.Authenticate(outsider.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"api/v1/projects/{project.Id}/scripts")).StatusCode);
        var forbiddenWrite = await f.Client.PutAsJsonAsync($"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(path)}", new SaveScriptDocumentRequest { ExpectedRevision = 0, Content = "class Attack {}" });
        Assert.Equal(HttpStatusCode.NotFound, forbiddenWrite.StatusCode);
    }

    [Fact]
    public async Task ScriptHistory_RestoreCreatesANewRevisionWithoutErasingLaterHistory()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "script-history@example.com"); f.Authenticate(owner.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Script History" });
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "History Project" });
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        const string path = "Assets/Scripts/Recovery.cs";
        var url = $"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(path)}";
        var first = await f.Client.PutAsJsonAsync(url, new SaveScriptDocumentRequest { ExpectedRevision = 0, Content = "class Recovery { int A; }" });
        var v1 = (await first.Content.ReadFromJsonAsync<ScriptDocumentSummary>())!;
        await f.Client.PutAsJsonAsync(url, new SaveScriptDocumentRequest { ExpectedRevision = 1, Content = "class Recovery { int B; }" });

        var history = await f.Client.GetFromJsonAsync<ProjectFileVersionSummary[]>($"api/v1/projects/{project.Id}/history?kind=script&path={Uri.EscapeDataString(path)}");
        Assert.Equal([2L, 1L], history!.Select(x => x.Revision));
        var restore = await f.Client.PostAsJsonAsync($"api/v1/projects/{project.Id}/history/restore", new RestoreProjectFileVersionRequest { Kind = "script", Path = path, SourceRevision = 1, ExpectedCurrentRevision = 2 });
        var restored = (await restore.Content.ReadFromJsonAsync<ProjectFileVersionSummary>())!;
        Assert.Equal(3, restored.Revision);
        var current = await f.Client.GetFromJsonAsync<ScriptDocumentSummary>(url);
        Assert.Equal(v1.Content, current!.Content);
        Assert.Equal(3, current.Revision);
        var staleRestore = await f.Client.PostAsJsonAsync($"api/v1/projects/{project.Id}/history/restore", new RestoreProjectFileVersionRequest { Kind = "script", Path = path, SourceRevision = 2, ExpectedCurrentRevision = 2 });
        Assert.Equal(HttpStatusCode.Conflict, staleRestore.StatusCode);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("Assets/../outside.cs")]
    [InlineData("C:/outside.cs")]
    [InlineData("Packages/com.example/Code.cs")]
    [InlineData("Assets/Scripts/Ship.txt")]
    public async Task ScriptDocuments_RejectUnsafeOrNonCSharpPaths(string path)
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "script-path@example.com"); f.Authenticate(owner.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Script Team" });
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Script Project" });
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        var response = await f.Client.PutAsJsonAsync($"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(path)}", new SaveScriptDocumentRequest { Content = "class X {}", ExpectedRevision = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
