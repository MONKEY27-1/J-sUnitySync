using System.Net.Http.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Projects;
using JsUnitySync.Desktop.Core.Teams;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace JsUnitySync.Desktop.Tests;

public sealed class TeamProjectWorkflowTests
{
    [Fact]
    public async Task Desktop_RegistersProjectAndLinksExplicitUnityFolderLocallyWithoutUploadingPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jsunitysync-team-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var api = JsUnitySync.Api.Program.CreateHost(["--urls", "http://127.0.0.1:0", "--environment", "Testing", $"--Database:ConnectionString=Data Source={Path.Combine(directory, "api.db")};Foreign Keys=True"]);
        try
        {
            await api.StartAsync(deadline.Token);
            var address = new Uri(api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).AbsoluteUri;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var collaboration = new CollaborationClient(http);
            collaboration.SetServer(address);
            var sessionStore = new ShellViewModelTests.MemorySessionStore();
            var account = new AccountViewModel(collaboration, sessionStore);
            var teams = new TeamWorkspaceViewModel(collaboration, account);
            var storePath = Path.Combine(directory, "device", "project-links.json");
            var projects = new ProjectWorkspaceViewModel(collaboration, account, teams, new JsonProjectLinkStore(storePath));
            try
            {
                account.Email = "dev@example.com";
                account.DisplayName = "Developer";
                account.RememberSession = true;
                await account.AuthenticateAsync("correct-horse-battery", register: true);
                Assert.True(account.IsSignedIn, account.Message);
                for (var i = 0; i < 100 && teams.IsBusy; i++) await Task.Delay(10, deadline.Token);
                teams.TeamName = "Developer Team";
                await teams.CreateTeamCommand.ExecuteAsync();
                Assert.True(teams.Teams.Count == 1, teams.Message);
                Assert.NotNull(teams.SelectedTeam);
                for (var i = 0; i < 100 && projects.IsBusy; i++) await Task.Delay(10, deadline.Token);
                await projects.RefreshAsync();
                projects.ProjectName = "Local Test Game";
                await projects.CreateCommand.ExecuteAsync();
                Assert.True(projects.Projects.Count == 1, $"{projects.Message}; team={teams.SelectedTeam?.Id}; selected project={projects.SelectedProject?.Id}");
                var remote = Assert.Single(projects.Projects);
                Assert.Equal("Local Test Game", remote.Name);

                var local = Path.Combine(directory, "The Unity Project");
                Directory.CreateDirectory(Path.Combine(local, "Assets"));
                Directory.CreateDirectory(Path.Combine(local, "ProjectSettings"));
                var version = Path.Combine(local, "ProjectSettings", "ProjectVersion.txt");
                await File.WriteAllTextAsync(version, "m_EditorVersion: 6000.0.1f1\n");
                var before = await File.ReadAllBytesAsync(version);
                await projects.LinkFolderAsync(local);
                Assert.Equal(Path.GetFullPath(local), projects.SelectedLocalPath);
                Assert.Contains("were not opened or modified", projects.Message);
                Assert.Equal(before, await File.ReadAllBytesAsync(version));

                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionStore.Session!.Token);
                var rawServerResponse = await http.GetStringAsync($"{address}api/v1/teams/{remote.TeamId}/projects");
                Assert.DoesNotContain(directory, rawServerResponse, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("LocalPath", rawServerResponse, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(remote.Id, rawServerResponse);
                Assert.True(File.Exists(version));

                await projects.UnlinkCommand.ExecuteAsync();
                Assert.Null(projects.SelectedLocalPath);
                Assert.True(Directory.Exists(local));
                await account.SignOutCommand.ExecuteAsync();
                Assert.False(account.IsSignedIn);
                Assert.Null(sessionStore.Session);
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.GetAsync($"{address}api/v1/me")).StatusCode);
            }
            finally { projects.Dispose(); teams.Dispose(); account.Dispose(); }
        }
        finally
        {
            await api.StopAsync(deadline.Token);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await api.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
