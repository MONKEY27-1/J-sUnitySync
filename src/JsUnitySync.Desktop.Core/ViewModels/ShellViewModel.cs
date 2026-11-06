using System.ComponentModel;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Teams;
using JsUnitySync.Desktop.Core.Projects;
using JsUnitySync.Desktop.Core.Sessions;
using JsUnitySync.Desktop.Core.Mvvm;
using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JsUnitySync.Desktop.Core.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly IServerHealthClient _health;
    private readonly ILogger<ShellViewModel> _logger;
    private NavigationPage _selectedPage;
    private DesktopSettings _savedSettings = new();
    private string _serverAddress = new DesktopSettings().ServerAddress;
    private bool _reduceMotion;
    private string _settingsMessage = "Settings are stored on this Windows account only.";
    private string _connectionStatus = "Not checked";
    private string _connectionMessage = "Check API availability. This does not join a session.";
    private string _lastChecked = "No check yet";
    private bool _disposed;

    public ShellViewModel(ISettingsStore settingsStore, IServerHealthClient health, ILogger<ShellViewModel> logger, AccountViewModel account, TeamWorkspaceViewModel teamWorkspace, ProjectWorkspaceViewModel projectWorkspace, SessionWorkspaceViewModel sessionWorkspace)
    {
        Account = account;
        TeamWorkspace = teamWorkspace;
        ProjectWorkspace = projectWorkspace;
        SessionWorkspace = sessionWorkspace;
        Account.PropertyChanged += OnAccountStateChanged;
        _settingsStore = settingsStore;
        _health = health;
        _logger = logger;
        Pages = Array.AsReadOnly(new[]
        {
            new NavigationPage("overview", "Overview", "\uE80F", "YOUR DEVELOPMENT WORKSPACE", "One place to build together.", "", "", "Phase 2 · Desktop foundation"),
            new NavigationPage("sessions", "Sessions", "\uE902", "COLLABORATION", "A shared workspace. Your local project. One accepted history.", "No session connected", "Session creation and joining arrive with real-time networking in Phase 7. The API check only verifies backend readiness; it does not connect to a collaboration session.", "Phase 7 · Real-time networking"),
            new NavigationPage("projects", "Projects", "\uE8B7", "YOUR WORK", "Register projects for your team and link your own local copies.", "", "", "Phase 5 · Project management"),
            new NavigationPage("team", "Team", "\uE716", "PEOPLE", "Manage membership and let recipients decide who they connect with.", "", "", "Phase 4 · Teams & invitations"),
            new NavigationPage("history", "History", "\uE81C", "ACCEPTED WORK", "A clear record. Recover without destroying newer work.", "Select a project to inspect accepted file versions", "Project script and asset revisions can be reviewed and restored as a new server-side revision. Local Unity files are never overwritten.", "Phase 14 · File history"),
            new NavigationPage("settings", "Settings", "\uE713", "MAKE IT YOURS", "A workspace that fits your workflow.", "", "", "Local preferences")
        });
        _selectedPage = Pages[0];
        foreach (var page in Pages)
            page.NavigateCommand = new RelayCommand(() => SelectedPage = page);
        OpenSettingsCommand = Pages[^1].NavigateCommand;
        CheckConnectionCommand = new AsyncCommand(CheckConnectionAsync, HandleCommandError);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => !CheckConnectionCommand.IsRunning && Account.IsIdle);
        CancelCheckCommand = new RelayCommand(CheckConnectionCommand.Cancel, () => CheckConnectionCommand.IsRunning);
        CheckConnectionCommand.PropertyChanged += OnCheckStateChanged;
    }

    public AccountViewModel Account { get; }
    public TeamWorkspaceViewModel TeamWorkspace { get; }
    public ProjectWorkspaceViewModel ProjectWorkspace { get; }
    public SessionWorkspaceViewModel SessionWorkspace { get; }
    public IReadOnlyList<NavigationPage> Pages { get; }
    public IEnumerable<NavigationPage> WorkspacePages => Pages.Skip(1).Take(4);
    public NavigationPage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || !Pages.Contains(value) || !Set(ref _selectedPage, value)) return;
            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(IsSettings));
            OnPropertyChanged(nameof(IsSessions));
            OnPropertyChanged(nameof(IsTeam));
            OnPropertyChanged(nameof(IsProjects));
            OnPropertyChanged(nameof(IsWorkspace));
            if (value.Key == "sessions") SessionWorkspace.RefreshCommand.Execute(null);
            if (value.Key == "team") TeamWorkspace.RefreshCommand.Execute(null);
            if (value.Key == "projects") ProjectWorkspace.RefreshCommand.Execute(null);
        }
    }
    public bool IsOverview => SelectedPage.Key == "overview";
    public bool IsSettings => SelectedPage.Key == "settings";
    public bool IsSessions => SelectedPage.Key == "sessions";
    public bool IsTeam => SelectedPage.Key == "team";
    public bool IsProjects => SelectedPage.Key == "projects";
    public bool IsWorkspace => SelectedPage.Key == "history";
    public string ServerAddress { get => _serverAddress; set => Set(ref _serverAddress, value); }
    public string SavedServerAddress => _savedSettings.ServerAddress;
    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }
    public string SettingsMessage { get => _settingsMessage; private set => Set(ref _settingsMessage, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string ConnectionMessage { get => _connectionMessage; private set => Set(ref _connectionMessage, value); }
    public string LastChecked { get => _lastChecked; private set => Set(ref _lastChecked, value); }
    public AsyncCommand CheckConnectionCommand { get; }
    public RelayCommand CancelCheckCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

    public void LoadSettings()
    {
        try
        {
            _savedSettings = _settingsStore.Load();
            ServerAddress = _savedSettings.ServerAddress;
            ReduceMotion = _savedSettings.ReduceMotion;
            Account.SetServer(_savedSettings.ServerAddress);
            OnPropertyChanged(nameof(SavedServerAddress));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Could not load desktop settings: {ErrorType}", error.GetType().Name);
            SettingsMessage = "Saved settings could not be read. Defaults are in use; the original file is unchanged. Save explicitly to replace it.";
        }
    }

    private void SaveSettings()
    {
        if (!ServerEndpoint.TryParse(ServerAddress, out var endpoint))
        {
            SettingsMessage = ServerEndpoint.ValidationMessage;
            return;
        }
        try
        {
            var settings = new DesktopSettings(endpoint.AbsoluteUri, ReduceMotion);
            _settingsStore.Save(settings);
            var changed = settings.ServerAddress != _savedSettings.ServerAddress;
            _savedSettings = settings;
            Account.SetServer(settings.ServerAddress);
            ServerAddress = settings.ServerAddress;
            OnPropertyChanged(nameof(SavedServerAddress));
            SettingsMessage = "Preferences saved to this Windows account.";
            if (changed)
            {
                ConnectionStatus = "Not checked";
                ConnectionMessage = "Server address changed. Run a new API check.";
                LastChecked = "No check yet";
            }
            _logger.LogInformation("Desktop preferences saved");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save desktop settings: {ErrorType}", error.GetType().Name);
            SettingsMessage = "Preferences could not be saved. Previous settings remain active. Check access to your LocalAppData folder.";
        }
    }

    private async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        ConnectionStatus = "Checking…";
        ConnectionMessage = "Contacting the saved server address. Up to 3 attempts; you can cancel.";
        try
        {
            var result = await _health.CheckAsync(new Uri(_savedSettings.ServerAddress), cancellationToken);
            ConnectionStatus = result.IsReady ? "API ready" : "Unavailable";
            ConnectionMessage = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionStatus = "Check cancelled";
            ConnectionMessage = "No session was opened. You can check again at any time.";
        }
        catch (Exception error)
        {
            HandleCommandError(error);
        }
        finally { LastChecked = $"Last check · {DateTimeOffset.Now:HH:mm:ss}"; }
    }

    private void HandleCommandError(Exception error)
    {
        _logger.LogError("Desktop command failed: {ErrorType}", error.GetType().Name);
        ConnectionStatus = "Check failed";
        ConnectionMessage = "An unexpected error occurred. Retry the check; see the desktop log for the error type.";
    }

    private void OnCheckStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AsyncCommand.IsRunning)) return;
        SaveSettingsCommand.NotifyCanExecuteChanged();
        CancelCheckCommand.NotifyCanExecuteChanged();
    }

    private void OnAccountStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Account.IsBusy)) SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Account.PropertyChanged -= OnAccountStateChanged;
        SessionWorkspace.Dispose();
        ProjectWorkspace.Dispose();
        TeamWorkspace.Dispose();
        Account.Dispose();
        CheckConnectionCommand.PropertyChanged -= OnCheckStateChanged;
        CheckConnectionCommand.Dispose();
    }
}
