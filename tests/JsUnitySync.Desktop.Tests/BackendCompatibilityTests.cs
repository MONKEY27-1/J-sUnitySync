using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Projects;
using JsUnitySync.Desktop.Core.Teams;
using JsUnitySync.Desktop.Core.Sessions;
using JsUnitySync.Desktop.Core.Realtime;
using JsUnitySync.Desktop.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace JsUnitySync.Desktop.Tests;

public sealed class BackendCompatibilityTests
{
    [Fact]
    public async Task Desktop_RealApiOfflineAndRecoveryRemainCompatible()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var api = Api.Program.CreateHost(["--urls", "http://127.0.0.1:0", "--environment", "Testing"]);
        await api.StartAsync(deadline.Token);
        var address = new Uri(api.Urls.Single()).AbsoluteUri;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new ServerHealthClient(http, NullLogger<ServerHealthClient>.Instance);
        var collaboration = new CollaborationClient(http);
        var account = new AccountViewModel(collaboration, new ShellViewModelTests.MemorySessionStore());
        var teams = new TeamWorkspaceViewModel(collaboration, account);
        var projects = new ProjectWorkspaceViewModel(collaboration, account, teams, new ShellViewModelTests.MemoryLinks());
        var realtimeStore = new ShellViewModelTests.MemoryRealtimeStore();
        var realtime = new RealtimeSessionClient(collaboration, account, realtimeStore);
        var sessions = new SessionWorkspaceViewModel(collaboration, account, projects, realtime, realtimeStore);
        using var shell = new Core.ViewModels.ShellViewModel(
            new ShellViewModelTests.MemorySettings { Settings = new DesktopSettings(address) },
            client, NullLogger<Core.ViewModels.ShellViewModel>.Instance, account, teams, projects, sessions);
        shell.LoadSettings();
        await shell.CheckConnectionCommand.ExecuteAsync();
        Assert.Equal("API ready", shell.ConnectionStatus);
        Assert.Contains("Collaboration is not connected", shell.ConnectionMessage);

        await api.StopAsync(deadline.Token);
        await shell.CheckConnectionCommand.ExecuteAsync();
        Assert.Equal("Unavailable", shell.ConnectionStatus);

        await using var restarted = Api.Program.CreateHost(["--urls", address, "--environment", "Testing"]);
        await restarted.StartAsync(deadline.Token);
        await shell.CheckConnectionCommand.ExecuteAsync();
        Assert.Equal("API ready", shell.ConnectionStatus);
        Assert.True(shell.CheckConnectionCommand.CanExecute(null));
        await restarted.StopAsync(deadline.Token);
    }
}
