using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Desktop.Core.Mvvm;
using JsUnitySync.Desktop.Core.Networking;

namespace JsUnitySync.Desktop.Core.Accounts;

public sealed class AccountViewModel : OperationViewModel, IDisposable
{
    private readonly CollaborationClient _client;
    private readonly ISessionStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private UserProfile? _profile;
    private string _email = "";
    private string _displayName = "";
    private bool _rememberSession;
    private bool _disposed;
    public AccountViewModel(CollaborationClient client, ISessionStore store)
    {
        _client = client;
        _store = store;
        _client.SessionInvalidated += OnSessionInvalidated;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync(), Unexpected);
        SaveProfileCommand = new AsyncCommand(_ => RunAsync(async () =>
        {
            if (Profile is null) return;
            SetProfile(await _client.SendAsync<UserProfile>(HttpMethod.Put, "api/v1/me", new ProfileUpdateRequest { DisplayName = DisplayName, ExpectedRevision = Profile.Revision }, _lifetime.Token));
            Message = "Profile saved.";
        }), Unexpected);
        SignOutCommand = new AsyncCommand(_ => RunAsync(async () =>
        {
            var message = "Signed out. Server session revoked.";
            try { await _client.SendAsync<object>(HttpMethod.Post, "api/v1/auth/logout", null, _lifetime.Token); }
            catch (ApiClientException) { message = "Signed out locally. Server revocation was not confirmed; that session expires within 12 hours."; }
            _client.ClearSession();
            Profile = null;
            DisplayName = "";
            try { _store.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Message = "Signed out locally, but the protected credential file could not be removed. This session may be restored on this Windows account until it expires or is revoked.";
                return;
            }
            Message = message;
        }), Unexpected);
    }
    public UserProfile? Profile { get => _profile; private set { if (Set(ref _profile, value)) { OnPropertyChanged(nameof(IsSignedIn)); OnPropertyChanged(nameof(IsSignedOut)); OnPropertyChanged(nameof(AccountLabel)); SessionChanged?.Invoke(this, EventArgs.Empty); } } }
    public bool IsSignedIn => Profile is not null;
    public bool IsSignedOut => !IsSignedIn;
    public string AccountLabel => Profile?.DisplayName ?? "Not signed in";
    public string Email { get => _email; set => Set(ref _email, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public bool RememberSession { get => _rememberSession; set => Set(ref _rememberSession, value); }
    public event EventHandler? SessionChanged;
    public AsyncCommand SaveProfileCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SignOutCommand { get; }

    public void SetServer(string address)
    {
        if (_client.ServerAddress == address) return;
        _client.SetServer(address);
        Profile = null;
        DisplayName = "";
        Message = "Server changed. Sign in to this server to continue.";
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task AuthenticateAsync(string password, bool register) => RunAsync(async () =>
    {
        var response = register
            ? await _client.SendAsync<AuthResponse>(HttpMethod.Post, "api/v1/auth/register", new RegisterRequest { Email = Email, DisplayName = DisplayName, Password = password }, _lifetime.Token, false)
            : await _client.SendAsync<AuthResponse>(HttpMethod.Post, "api/v1/auth/login", new LoginRequest { Email = Email, Password = password }, _lifetime.Token, false);
        var session = new StoredSession(_client.ServerAddress, response.Token, response.ExpiresAt);
        _client.SetSession(session);
        SetProfile(response.Profile);
        try
        {
            if (RememberSession) _store.Save(session); else _store.Delete();
            Message = "Signed in. This does not join a collaboration session.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Message = "Signed in for this run, but saved credentials could not be updated. Check LocalAppData access."; }
    });

    public Task RestoreAsync() => RunAsync(async () =>
    {
        StoredSession? saved;
        try { saved = _store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { Message = "Saved credentials could not be read. Sign in again; no account password was stored."; return; }
        if (saved is null || saved.ServerAddress != _client.ServerAddress || saved.ExpiresAt <= DateTimeOffset.UtcNow) return;
        _client.SetSession(saved);
        SetProfile(await _client.SendAsync<UserProfile>(HttpMethod.Get, "api/v1/me", null, _lifetime.Token));
        RememberSession = true;
        Message = "Session restored and validated with the server.";
    });

    public Task RefreshAsync() => RunAsync(async () =>
    {
        SetProfile(await _client.SendAsync<UserProfile>(HttpMethod.Get, "api/v1/me", null, _lifetime.Token));
        Message = "Profile refreshed. Unsaved name edits were replaced by the accepted server value.";
    });

    private void SetProfile(UserProfile profile)
    {
        Profile = profile;
        Email = profile.Email;
        DisplayName = profile.DisplayName;
    }
    private void ClearLocalSession()
    {
        _client.ClearSession();
        Profile = null;
        DisplayName = "";
        _store.Delete();
    }
    private void OnSessionInvalidated(object? sender, EventArgs e)
    {
        Profile = null;
        DisplayName = "";
        try { _store.Delete(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Message = "Session expired or revoked. Sign in again.";
    }
    private void Unexpected(Exception _) => Message = "Unexpected account error. Refresh before retrying.";
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.SessionInvalidated -= OnSessionInvalidated;
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommand.Dispose();
        SaveProfileCommand.Dispose();
        SignOutCommand.Dispose();
    }
}
