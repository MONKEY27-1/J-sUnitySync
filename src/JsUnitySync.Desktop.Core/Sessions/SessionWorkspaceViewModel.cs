using System.Collections.ObjectModel;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Mvvm;
using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Projects;
using JsUnitySync.Desktop.Core.Realtime;

namespace JsUnitySync.Desktop.Core.Sessions;

public sealed class SessionWorkspaceViewModel : OperationViewModel, IDisposable
{
    private readonly CollaborationClient _client;
    private readonly AccountViewModel _account;
    private readonly ProjectWorkspaceViewModel _projects;
    private readonly RealtimeSessionClient _realtime;
    private readonly IRealtimeClientStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private SessionSummary? _selectedSession;
    private string _sessionName = "";
    private string _connectionStatus = "Not connected";
    private long _storedSequence = -1;
    private int _storedEventCount;
    private bool _connected;
    private bool _disposed;

    public SessionWorkspaceViewModel(CollaborationClient client, AccountViewModel account, ProjectWorkspaceViewModel projects,
        RealtimeSessionClient realtime, IRealtimeClientStore store)
    {
        _client = client; _account = account; _projects = projects; _realtime = realtime; _store = store;
        Sessions = [];
        _projects.PropertyChanged += ProjectChanged;
        _account.SessionChanged += AccountChanged;
        RefreshCommand = new AsyncCommand(_ => RunAsync(RefreshAsync), Fail);
        CreateCommand = new AsyncCommand(_ => RunAsync(CreateAsync), Fail);
        ConnectCommand = new AsyncCommand(ConnectAsync, Fail);
        DisconnectCommand = new RelayCommand(ConnectCommand.Cancel, () => ConnectCommand.IsRunning);
        ConnectCommand.PropertyChanged += ConnectStateChanged;
    }

    public ObservableCollection<SessionSummary> Sessions { get; }
    public SessionSummary? SelectedSession { get => _selectedSession; set { if (Set(ref _selectedSession, value)) RefreshLocalCursor(); } }
    public string SessionName { get => _sessionName; set => Set(ref _sessionName, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public long StoredSequence { get => _storedSequence; private set => Set(ref _storedSequence, value); }
    public int StoredEventCount { get => _storedEventCount; private set => Set(ref _storedEventCount, value); }
    public bool IsConnected { get => _connected; private set => Set(ref _connected, value); }
    public bool IsSignedIn => _account.IsSignedIn;
    public bool CanCreateSession => _account.IsSignedIn && _projects.SelectedProject is { IsArchived: false };
    public bool CanConnect => _account.IsSignedIn && SelectedSession is not null && !ConnectCommand.IsRunning;
    public ProjectWorkspaceViewModel Projects => _projects;
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand CreateCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    public async Task RefreshAsync()
    {
        if (!_account.IsSignedIn || _projects.SelectedProject is not { } project)
        {
            Sessions.Clear(); SelectedSession = null;
            Message = !_account.IsSignedIn ? "Sign in to view or create collaboration sessions." : "Select an active team project before creating a session.";
            return;
        }
        var selectedId = SelectedSession?.Id;
        var sessions = await _client.SendAsync<SessionSummary[]>(HttpMethod.Get, $"api/v1/projects/{project.Id}/sessions", null, _lifetime.Token);
        Sessions.Clear(); foreach (var session in sessions) Sessions.Add(session);
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == selectedId) ?? Sessions.FirstOrDefault();
        Message = $"{Sessions.Count} session(s) for {project.Name}. Sessions do not modify project files.";
    }

    private async Task CreateAsync()
    {
        if (!_account.IsSignedIn || _projects.SelectedProject is not { IsArchived: false } project)
        { Message = "Sign in and select an active project first."; return; }
        var created = await _client.SendAsync<SessionSummary>(HttpMethod.Post, $"api/v1/projects/{project.Id}/sessions",
            new CreateSessionRequest { Name = SessionName }, _lifetime.Token);
        SessionName = ""; Sessions.Insert(0, created); SelectedSession = created;
        Message = "Shared session created on the server. Connect to receive its durable ordered event stream.";
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (!_account.IsSignedIn || SelectedSession is null) throw new ApiClientException(401, "Sign in and select a session first.");
        var sessionId = SelectedSession.Id;
        IsConnected = true;
        ConnectionStatus = "Connecting…";
        try { await _realtime.RunAsync(sessionId, text => ConnectionStatus = text, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ConnectionStatus = "Disconnected. Durable local events are preserved."; }
        catch (ApiClientException error)
        { ConnectionStatus = error.Message; throw; }
        catch (RealtimeStorageException error)
        { ConnectionStatus = error.Message; throw; }
        finally { IsConnected = false; OnPropertyChanged(nameof(CanConnect)); }
    }

    private void RefreshLocalCursor()
    {
        if (SelectedSession is null || _account.Profile is null)
        { StoredSequence = -1; StoredEventCount = 0; OnPropertyChanged(nameof(CanConnect)); return; }
        try
        {
            var cursor = _store.GetOrCreateCursor(_client.ServerAddress, _account.Profile.Id, SelectedSession.Id);
            var snapshot = _store.GetSnapshot(_client.ServerAddress, _account.Profile.Id, SelectedSession.Id);
            StoredSequence = cursor.LastAckedSequence;
            StoredEventCount = snapshot.AcceptedEventCount;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { ConnectionStatus = "Protected local event inbox could not be read; the server cursor was not advanced."; }
        OnPropertyChanged(nameof(CanConnect));
    }

    private void ProjectChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ProjectWorkspaceViewModel.SelectedProject)) { OnPropertyChanged(nameof(CanCreateSession)); RefreshCommand.Execute(null); } }
    private void AccountChanged(object? sender, EventArgs e)
    { OnPropertyChanged(nameof(IsSignedIn)); OnPropertyChanged(nameof(CanCreateSession)); RefreshCommand.Execute(null); }
    private void ConnectStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AsyncCommand.IsRunning)) return;
        OnPropertyChanged(nameof(CanConnect)); DisconnectCommand.NotifyCanExecuteChanged();
    }
    private void Fail(Exception error) => Message = error switch
    {
        ApiClientException api => api.Message,
        RealtimeStorageException storage => storage.Message,
        _ => "Session operation failed. Refresh before retrying."
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projects.PropertyChanged -= ProjectChanged;
        _account.SessionChanged -= AccountChanged;
        ConnectCommand.PropertyChanged -= ConnectStateChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
        RefreshCommand.Dispose(); CreateCommand.Dispose(); ConnectCommand.Dispose();
    }
}
