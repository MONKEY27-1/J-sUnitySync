using System.Collections.ObjectModel;
using JsUnitySync.Contracts.Teams;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Mvvm;
using JsUnitySync.Desktop.Core.Networking;

namespace JsUnitySync.Desktop.Core.Teams;

public sealed class TeamWorkspaceViewModel : OperationViewModel, IDisposable
{
    private readonly CollaborationClient _client;
    private readonly AccountViewModel _account;
    private readonly CancellationTokenSource _lifetime = new();
    private string _teamName = "";
    private string _inviteUserId = "";
    private string _friendUserId = "";
    private TeamSummary? _selectedTeam;
    private bool _disposed;

    public TeamWorkspaceViewModel(CollaborationClient client, AccountViewModel account)
    {
        _client = client; _account = account;
        _account.SessionChanged += SessionChanged;
        Teams = []; Members = []; Invitations = []; Friends = []; FriendRequests = [];
        RefreshCommand = new AsyncCommand(_ => RunAsync(RefreshAsync), Fail);
        CreateTeamCommand = new AsyncCommand(_ => RunAsync(CreateTeamAsync), Fail);
        InviteCommand = new AsyncCommand(_ => RunAsync(InviteAsync), Fail);
        SendFriendRequestCommand = new AsyncCommand(_ => RunAsync(SendFriendRequestAsync), Fail);
        AcceptInvitationCommand = new AsyncParameterCommand<TeamInvitationSummary>(invitation => RunAsync(() => RespondInvitation(invitation, true)), Fail);
        DeclineInvitationCommand = new AsyncParameterCommand<TeamInvitationSummary>(invitation => RunAsync(() => RespondInvitation(invitation, false)), Fail);
        AcceptFriendRequestCommand = new AsyncParameterCommand<FriendRequestSummary>(request => RunAsync(() => RespondFriend(request, true)), Fail);
        DeclineFriendRequestCommand = new AsyncParameterCommand<FriendRequestSummary>(request => RunAsync(() => RespondFriend(request, false)), Fail);
        ChangeRoleCommand = new AsyncParameterCommand<TeamMemberSummary>(member => RunAsync(() => ChangeRole(member)), Fail);
    }

    public ObservableCollection<TeamSummary> Teams { get; }
    public ObservableCollection<TeamMemberSummary> Members { get; }
    public ObservableCollection<TeamInvitationSummary> Invitations { get; }
    public ObservableCollection<FriendProfile> Friends { get; }
    public ObservableCollection<FriendRequestSummary> FriendRequests { get; }
    public TeamSummary? SelectedTeam { get => _selectedTeam; set { if (Set(ref _selectedTeam, value)) { OnPropertyChanged(nameof(CanManageSelectedTeam)); OnPropertyChanged(nameof(CanInviteSelectedTeam)); _ = RunAsync(LoadMembersAsync); } } }
    public bool CanManageSelectedTeam => SelectedTeam?.Role == 2;
    public bool CanInviteSelectedTeam => SelectedTeam?.Role >= 1;
    public string TeamName { get => _teamName; set => Set(ref _teamName, value); }
    public string InviteUserId { get => _inviteUserId; set => Set(ref _inviteUserId, value); }
    public string FriendUserId { get => _friendUserId; set => Set(ref _friendUserId, value); }
    public bool IsSignedIn => _account.IsSignedIn;
    public bool IsSignedOut => !_account.IsSignedIn;
    public string SignedOutMessage => "Sign in to create a team, invite people or manage friends.";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand CreateTeamCommand { get; }
    public AsyncCommand InviteCommand { get; }
    public AsyncCommand SendFriendRequestCommand { get; }
    public AsyncParameterCommand<TeamInvitationSummary> AcceptInvitationCommand { get; }
    public AsyncParameterCommand<TeamInvitationSummary> DeclineInvitationCommand { get; }
    public AsyncParameterCommand<FriendRequestSummary> AcceptFriendRequestCommand { get; }
    public AsyncParameterCommand<FriendRequestSummary> DeclineFriendRequestCommand { get; }
    public AsyncParameterCommand<TeamMemberSummary> ChangeRoleCommand { get; }

    public async Task RefreshAsync()
    {
        if (!_account.IsSignedIn)
        {
            Teams.Clear(); Members.Clear(); Invitations.Clear(); Friends.Clear(); FriendRequests.Clear(); SelectedTeam = null;
            Message = SignedOutMessage;
            return;
        }
        var selectedId = SelectedTeam?.Id;
        var teams = await _client.SendAsync<TeamSummary[]>(HttpMethod.Get, "api/v1/me/teams", null, _lifetime.Token);
        Replace(Teams, teams);
        SelectedTeam = Teams.FirstOrDefault(team => team.Id == selectedId) ?? Teams.FirstOrDefault();
        await LoadMembersAsync();
        Replace(Invitations, await _client.SendAsync<TeamInvitationSummary[]>(HttpMethod.Get, "api/v1/me/invitations", null, _lifetime.Token));
        Replace(Friends, await _client.SendAsync<FriendProfile[]>(HttpMethod.Get, "api/v1/me/friends", null, _lifetime.Token));
        Replace(FriendRequests, await _client.SendAsync<FriendRequestSummary[]>(HttpMethod.Get, "api/v1/me/friend-requests", null, _lifetime.Token));
        Message = "Team and social data refreshed from the server.";
    }

    private async Task CreateTeamAsync()
    {
        var team = await _client.SendAsync<TeamSummary>(HttpMethod.Post, "api/v1/teams", new CreateTeamRequest { Name = TeamName }, _lifetime.Token);
        TeamName = ""; SelectedTeam = team; await RefreshAsync(); Message = "Team created. You are its owner.";
    }
    private async Task InviteAsync()
    {
        if (SelectedTeam is null) { Message = "Select a team first."; return; }
        await _client.SendAsync<TeamInvitationSummary>(HttpMethod.Post, $"api/v1/teams/{SelectedTeam.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = InviteUserId.Trim() }, _lifetime.Token);
        InviteUserId = ""; Message = "Invitation created. The recipient must sign in and accept it.";
        await RefreshAsync();
    }
    private async Task SendFriendRequestAsync()
    {
        await _client.SendAsync<object>(HttpMethod.Post, "api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = FriendUserId.Trim() }, _lifetime.Token);
        FriendUserId = ""; Message = "Friend request sent. The recipient decides whether to accept.";
        await RefreshAsync();
    }
    private async Task RespondInvitation(TeamInvitationSummary invitation, bool accept)
    {
        await _client.SendAsync<object>(HttpMethod.Post, $"api/v1/me/invitations/{invitation.Id}/{(accept ? "accept" : "decline")}", new InvitationResponseRequest { ExpectedRevision = invitation.Revision }, _lifetime.Token);
        Message = accept ? "Invitation accepted. Team access is active." : "Invitation declined."; await RefreshAsync();
    }
    private async Task ChangeRole(TeamMemberSummary member)
    {
        if (SelectedTeam is null) return;
        var desiredRole = member.Role == 1 ? 0 : 1;
        var updated = await _client.SendAsync<TeamMemberSummary>(HttpMethod.Put, $"api/v1/teams/{SelectedTeam.Id}/members/{member.UserId}/role", new ChangeMemberRoleRequest { Role = desiredRole, ExpectedRevision = member.Revision }, _lifetime.Token);
        Message = $"{updated.DisplayName}'s role changed to {updated.RoleName}.";
        await LoadMembersAsync();
    }
    private async Task RespondFriend(FriendRequestSummary request, bool accept)
    {
        await _client.SendAsync<object>(HttpMethod.Post, $"api/v1/me/friend-requests/{request.Id}/{(accept ? "accept" : "decline")}", new InvitationResponseRequest { ExpectedRevision = request.Revision }, _lifetime.Token);
        Message = accept ? "Friend request accepted." : "Friend request declined."; await RefreshAsync();
    }
    private async Task LoadMembersAsync()
    {
        if (!_account.IsSignedIn || SelectedTeam is null) { Members.Clear(); return; }
        try { Replace(Members, await _client.SendAsync<TeamMemberSummary[]>(HttpMethod.Get, $"api/v1/teams/{SelectedTeam.Id}/members", null, _lifetime.Token)); }
        catch (Exception error) when (error is ApiClientException or OperationCanceledException) { Members.Clear(); if (error is ApiClientException) Message = error.Message; }
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    { target.Clear(); foreach (var item in items) target.Add(item); }
    private void SessionChanged(object? sender, EventArgs args)
    { OnPropertyChanged(nameof(IsSignedIn)); OnPropertyChanged(nameof(IsSignedOut)); RefreshCommand.Execute(null); }
    private void Fail(Exception error) => Message = error is ApiClientException api ? api.Message : "Operation failed. Refresh before retrying.";
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _account.SessionChanged -= SessionChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
        RefreshCommand.Dispose(); CreateTeamCommand.Dispose(); InviteCommand.Dispose(); SendFriendRequestCommand.Dispose();
    }
}
