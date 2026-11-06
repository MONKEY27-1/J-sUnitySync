using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Contracts.Teams;

namespace JsUnitySync.Server.Tests;

public sealed class ProjectAssetTests
{
    private static async Task<AuthResponse> User(ApiFixture f, string email)
    {
        var response = await f.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = "Asset User", Password = "correct-horse-battery" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task Assets_AuthorizedUploadUsesContentHashAndDownloadReturnsExactBytes()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "asset-owner@example.com"); f.Authenticate(owner.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Asset Team" });
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Asset Project" });
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        const string path = "Assets/Models/ship.bin";
        var original = new byte[] { 0, 1, 2, 3, 4, 0xff, 0x80 };
        using var upload = new ByteArrayContent(original);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var put = await f.Client.PutAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString(path)}&expectedRevision=0", upload);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var asset = (await put.Content.ReadFromJsonAsync<ProjectAssetSummary>())!;
        Assert.Equal(1, asset.Revision);
        Assert.Equal(original.LongLength, asset.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), asset.ContentHash);

        var get = await f.Client.GetAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal($"\"{asset.ContentHash}\"", get.Headers.ETag!.Tag);
        Assert.Equal(original, await get.Content.ReadAsByteArrayAsync());
        var listed = Assert.Single((await f.Client.GetFromJsonAsync<ProjectAssetSummary[]>($"api/v1/projects/{project.Id}/assets"))!);
        Assert.Equal(path, listed.Path);

        var largerBytes = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
        using var largerUpload = new ByteArrayContent(largerBytes);
        var largerResponse = await f.Client.PutAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString("Assets/Models/large.bin")}&expectedRevision=0", largerUpload);
        Assert.Equal(HttpStatusCode.OK, largerResponse.StatusCode);
        var largerMetadata = (await largerResponse.Content.ReadFromJsonAsync<ProjectAssetSummary>())!;
        Assert.Equal(largerBytes.LongLength, largerMetadata.Length);
        Assert.Equal(largerBytes, await f.Client.GetByteArrayAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString("Assets/Models/large.bin")}"));

        using var staleBody = new ByteArrayContent([9]);
        var stale = await f.Client.PutAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString(path)}&expectedRevision=0", staleBody);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task AssetHistory_RestoreReferencesPriorBlobAsANewRevision()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "asset-history@example.com"); f.Authenticate(owner.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Asset History" });
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "History Project" });
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        const string path = "Assets/Textures/ship.dat";
        var uploadUrl = $"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString(path)}&expectedRevision=";
        using var one = new ByteArrayContent([1, 2, 3]);
        var first = await f.Client.PutAsync(uploadUrl + "0", one);
        var v1 = (await first.Content.ReadFromJsonAsync<ProjectAssetSummary>())!;
        using var two = new ByteArrayContent([4, 5, 6]);
        await f.Client.PutAsync(uploadUrl + "1", two);

        var history = await f.Client.GetFromJsonAsync<ProjectFileVersionSummary[]>($"api/v1/projects/{project.Id}/history?kind=asset&path={Uri.EscapeDataString(path)}");
        Assert.Equal([2L, 1L], history!.Select(x => x.Revision));
        var restore = await f.Client.PostAsJsonAsync($"api/v1/projects/{project.Id}/history/restore", new RestoreProjectFileVersionRequest { Kind = "asset", Path = path, SourceRevision = 1, ExpectedCurrentRevision = 2 });
        var restored = (await restore.Content.ReadFromJsonAsync<ProjectFileVersionSummary>())!;
        Assert.Equal(3, restored.Revision);
        Assert.Equal(v1.ContentHash, restored.ContentHash);
        var content = await f.Client.GetByteArrayAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString(path)}");
        Assert.Equal(new byte[] { 1, 2, 3 }, content);
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("Assets/../escape.bin")]
    [InlineData("C:/outside.bin")]
    [InlineData("Assets/Scripts/Main.cs")]
    public async Task Assets_RejectPathsOutsideContentScope(string path)
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await User(f, "asset-path@example.com"); f.Authenticate(owner.Token);
        var teamResponse = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = "Asset Team" });
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamSummary>())!;
        var projectResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = "Asset Project" });
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
        using var body = new ByteArrayContent([1]);
        var response = await f.Client.PutAsync($"api/v1/projects/{project.Id}/assets/content?path={Uri.EscapeDataString(path)}&expectedRevision=0", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
