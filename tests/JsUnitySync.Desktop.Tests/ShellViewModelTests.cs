using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Projects;
using JsUnitySync.Desktop.Core.Teams;
using JsUnitySync.Desktop.Core.Sessions;
using JsUnitySync.Desktop.Core.Realtime;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Desktop.Core.Settings;
using JsUnitySync.Desktop.Core.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace JsUnitySync.Desktop.Tests;

public sealed class ShellViewModelTests
{
    internal sealed class MemorySettings : ISettingsStore
    {
        public DesktopSettings Settings { get; set; } = new();
        public Exception? LoadError { get; set; }
        public bool FailSave { get; set; }
        public DesktopSettings Load() => LoadError is null ? Settings : throw LoadError;
        public void Save(DesktopSettings settings)
        {
            if (FailSave) throw new IOException("Access denied for test");
            Settings = settings;
        }
    }

    internal sealed class HealthStub : IServerHealthClient
    {
        public Func<Uri, CancellationToken, Task<HealthCheckResult>> Handler { get; set; } =
            (_, _) => Task.FromResult(new HealthCheckResult(true, "API ready; no session."));
        public Task<HealthCheckResult> CheckAsync(Uri server, CancellationToken cancellationToken) => Handler(server, cancellationToken);
    }

    internal static AccountViewModel CreateAccount() => new(new CollaborationClient(new HttpClient()), new MemorySessionStore());
    internal sealed class MemoryLinks : IProjectLinkStore
    {
        private readonly List<LocalProjectLink> _links = [];
        public IReadOnlyList<LocalProjectLink> Load(string server, string user) => _links.Where(x => x.ServerAddress == server && x.UserId == user).ToArray();
        public void Add(LocalProjectLink link) => _links.Add(link);
        public void Remove(string server, string user, string project) => _links.RemoveAll(x => x.ServerAddress == server && x.UserId == user && x.ProjectId == project);
    }
    internal sealed class MemoryRealtimeStore : IRealtimeClientStore
    {
        private readonly Dictionary<string, (RealtimeCursor Cursor, List<WireFrame> Events, List<WireFrame> Conflicts)> _states = [];
        private static string Key(string s, string u, string session) => $"{s}|{u}|{session}";
        public RealtimeCursor GetOrCreateCursor(string serverAddress, string userId, string sessionId)
        {
            var key = Key(serverAddress, userId, sessionId);
            if (!_states.TryGetValue(key, out var state)) { state = (new(Guid.NewGuid().ToString("N"), -1), [], []); _states[key] = state; }
            return state.Cursor;
        }
        public RealtimeInboxSnapshot GetSnapshot(string serverAddress, string userId, string sessionId)
        { var state = _states.GetValueOrDefault(Key(serverAddress, userId, sessionId)); return state.Cursor is null ? new(-1, 0, [], []) : new(state.Cursor.LastAckedSequence, state.Events.Count, state.Events, state.Conflicts); }
        public Task<long> StoreAcceptedEventAsync(string serverAddress, string userId, string sessionId, string clientId, WireFrame frame, CancellationToken cancellationToken)
        { var key = Key(serverAddress, userId, sessionId); var state = _states[key]; state.Events.Add(frame); state.Cursor = state.Cursor with { LastAckedSequence = frame.Sequence }; _states[key] = state; return Task.FromResult(frame.Sequence); }
        public Task StoreConflictAsync(string serverAddress, string userId, string sessionId, string clientId, WireFrame frame, CancellationToken cancellationToken)
        { var key = Key(serverAddress, userId, sessionId); var state = _states[key]; state.Conflicts.Add(frame); _states[key] = state; return Task.CompletedTask; }
    }
    internal static ShellViewModel Create(MemorySettings? settings = null, HealthStub? health = null)
    {
        var collaboration = new CollaborationClient(new HttpClient());
        var account = new AccountViewModel(collaboration, new MemorySessionStore());
        var teams = new TeamWorkspaceViewModel(collaboration, account);
        var projects = new ProjectWorkspaceViewModel(collaboration, account, teams, new MemoryLinks());
        var realtimeStore = new MemoryRealtimeStore();
        var realtime = new RealtimeSessionClient(collaboration, account, realtimeStore);
        var sessions = new SessionWorkspaceViewModel(collaboration, account, projects, realtime, realtimeStore);
        return new(settings ?? new(), health ?? new(), NullLogger<ShellViewModel>.Instance, account, teams, projects, sessions);
    }

    internal sealed class MemorySessionStore : ISessionStore
    {
        public StoredSession? Session { get; private set; }
        public StoredSession? Load() => Session;
        public void Save(StoredSession session) => Session = session;
        public void Delete() => Session = null;
    }

    [Fact]
    public void Navigation_AllPagesRaiseNotificationsAndSelectCorrectPanel()
    {
        using var vm = Create();
        Assert.True(vm.IsOverview);
        Assert.Equal(6, vm.Pages.Count);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        foreach (var page in vm.Pages.Skip(1))
        {
            page.NavigateCommand.Execute(null);
            Assert.Same(page, vm.SelectedPage);
            Assert.False(vm.IsOverview);
            Assert.Equal(page.Key == "settings", vm.IsSettings);
            Assert.Equal(page.Key == "sessions", vm.IsSessions);
            Assert.Equal(page.Key == "history", vm.IsWorkspace);
        }
        Assert.Contains(nameof(vm.SelectedPage), changes);
        Assert.Contains(nameof(vm.IsSettings), changes);
        Assert.Equal(4, vm.WorkspacePages.Count());
    }

    [Fact]
    public void LoadSettings_RestoresEndpointAndAppearance()
    {
        using var vm = Create(new() { Settings = new("https://sync.example.com/", true) });
        vm.LoadSettings();
        Assert.Equal("https://sync.example.com/", vm.ServerAddress);
        Assert.Equal(vm.ServerAddress, vm.SavedServerAddress);
        Assert.True(vm.ReduceMotion);
    }

    [Fact]
    public void LoadSettings_CorruptFileUsesDefaultsAndDoesNotOverwrite()
    {
        var store = new MemorySettings { LoadError = new InvalidDataException("Invalid settings") };
        using var vm = Create(store);
        vm.LoadSettings();
        Assert.Equal(new DesktopSettings().ServerAddress, vm.SavedServerAddress);
        Assert.Contains("original file is unchanged", vm.SettingsMessage);
        Assert.Equal(new DesktopSettings(), store.Settings);
    }

    [Fact]
    public void SaveSettings_InvalidEndpointNeverReplacesAcceptedSettings()
    {
        var store = new MemorySettings();
        using var vm = Create(store);
        vm.ServerAddress = "http://remote.example.com";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal(ServerEndpoint.ValidationMessage, vm.SettingsMessage);
        Assert.Equal(new DesktopSettings(), store.Settings);
    }

    [Fact]
    public void SaveSettings_WriteFailurePreservesActiveEndpoint()
    {
        using var vm = Create(new() { FailSave = true });
        vm.ServerAddress = "https://sync.example.com";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal(new DesktopSettings().ServerAddress, vm.SavedServerAddress);
        Assert.Contains("Previous settings remain active", vm.SettingsMessage);
    }

    [Fact]
    public async Task SaveSettings_ChangingServerInvalidatesOldReadiness()
    {
        using var vm = Create();
        await vm.CheckConnectionCommand.ExecuteAsync();
        Assert.Equal("API ready", vm.ConnectionStatus);
        vm.ServerAddress = " https://sync.example.com ";
        vm.ReduceMotion = true;
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("https://sync.example.com/", vm.SavedServerAddress);
        Assert.Equal("Not checked", vm.ConnectionStatus);
        Assert.Equal("No check yet", vm.LastChecked);
    }

    [Fact]
    public async Task Check_UsesSavedNotUnsavedAddressAndReportsOffline()
    {
        Uri? requested = null;
        using var vm = Create(health: new()
        {
            Handler = (uri, _) =>
            {
                requested = uri;
                return Task.FromResult(new HealthCheckResult(false, "Offline; retry."));
            }
        });
        vm.ServerAddress = "https://unsaved.example.com";
        await vm.CheckConnectionCommand.ExecuteAsync();
        Assert.Equal(new DesktopSettings().ServerAddress, requested!.AbsoluteUri);
        Assert.Equal("Unavailable", vm.ConnectionStatus);
        Assert.Equal("Offline; retry.", vm.ConnectionMessage);
    }

    [Fact]
    public async Task Check_CancellationReenablesCommandsAndNeverClaimsConnected()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = Create(health: new()
        {
            Handler = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(true, "unreachable");
            }
        });
        var checking = vm.CheckConnectionCommand.ExecuteAsync();
        await started.Task;
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.True(vm.CancelCheckCommand.CanExecute(null));
        vm.CancelCheckCommand.Execute(null);
        await checking;
        Assert.Equal("Check cancelled", vm.ConnectionStatus);
        Assert.True(vm.SaveSettingsCommand.CanExecute(null));
        Assert.True(vm.CheckConnectionCommand.CanExecute(null));
        Assert.False(vm.CancelCheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Check_UnexpectedFailureIsSurfacedWithoutCrash()
    {
        using var vm = Create(health: new() { Handler = (_, _) => throw new InvalidOperationException("test") });
        await vm.CheckConnectionCommand.ExecuteAsync();
        Assert.Equal("Check failed", vm.ConnectionStatus);
        Assert.True(vm.CheckConnectionCommand.CanExecute(null));
    }
}
