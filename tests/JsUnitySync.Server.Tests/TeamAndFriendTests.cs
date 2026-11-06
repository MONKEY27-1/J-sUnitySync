using System.Net;
using System.Net.Http.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Contracts.Teams;

namespace JsUnitySync.Server.Tests;

public sealed class TeamAndFriendTests
{
    private static async Task<AuthResponse> NewUser(ApiFixture fixture, string email)
    {
        var response = await fixture.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = email[..email.IndexOf('@')], Password = "correct-horse-battery" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
    private static void As(ApiFixture fixture, AuthResponse? user) => fixture.Authenticate(user?.Token);
    private static async Task<TeamSummary> NewTeam(ApiFixture f, string name = "Runtime Team")
    {
        var response = await f.Client.PostAsJsonAsync("api/v1/teams", new CreateTeamRequest { Name = name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamSummary>())!;
    }

    [Fact]
    public async Task Team_OwnerInvitesAccountAndRecipientMustAcceptBeforeMembership()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await NewUser(f, "owner@example.com");
        var invitee = await NewUser(f, "invitee@example.com");
        As(f, owner);
        var team = await NewTeam(f);
        Assert.Equal(2, team.Role); Assert.Equal(1, team.MemberCount);
        var inviteResponse = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = invitee.Profile.Id });
        Assert.Equal(HttpStatusCode.OK, inviteResponse.StatusCode);
        var invitation = (await inviteResponse.Content.ReadFromJsonAsync<TeamInvitationSummary>())!;
        var duplicate = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = invitee.Profile.Id });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single((await f.Client.GetFromJsonAsync<TeamSummary[]>("api/v1/me/teams"))!);

        As(f, invitee);
        var pending = await f.Client.GetFromJsonAsync<TeamInvitationSummary[]>("api/v1/me/invitations");
        Assert.Single(pending!); Assert.Equal(owner.Profile.DisplayName, pending![0].InviterName);
        var notMember = await f.Client.GetAsync($"api/v1/teams/{team.Id}/members");
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/accept", new InvitationResponseRequest { ExpectedRevision = invitation.Revision })).StatusCode);
        var teams = await f.Client.GetFromJsonAsync<TeamSummary[]>("api/v1/me/teams");
        Assert.Equal(0, Assert.Single(teams!).Role);
        As(f, owner);
        var members = await f.Client.GetFromJsonAsync<TeamMemberSummary[]>($"api/v1/teams/{team.Id}/members");
        Assert.Equal(2, members!.Length);
        Assert.DoesNotContain(members, member => member.DisplayName.Contains('@'));
    }

    [Fact]
    public async Task Team_UnknownAndNonMemberAccessAreIndistinguishable()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await NewUser(f, "owner@example.com");
        var outsider = await NewUser(f, "outsider@example.com");
        As(f, owner); var team = await NewTeam(f);
        As(f, outsider);
        var otherTeam = await f.Client.GetAsync($"api/v1/teams/{team.Id}/members");
        var unknownTeam = await f.Client.GetAsync("api/v1/teams/01ARZ3NDEKTSV4RRFFQ69G5FAV/members");
        Assert.Equal(HttpStatusCode.NotFound, otherTeam.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownTeam.StatusCode);
        Assert.Equal(await otherTeam.Content.ReadAsStringAsync(), await unknownTeam.Content.ReadAsStringAsync());
        var invite = await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = owner.Profile.Id });
        Assert.Equal(HttpStatusCode.NotFound, invite.StatusCode);
    }

    [Fact]
    public async Task Team_InvitationIsRecipientOnlyAndVersioned()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await NewUser(f, "owner@example.com");
        var invitee = await NewUser(f, "invitee@example.com");
        As(f, owner); var team = await NewTeam(f);
        var invitation = (await (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = invitee.Profile.Id })).Content.ReadFromJsonAsync<TeamInvitationSummary>())!;
        As(f, owner);
        var ownerCannotAccept = await f.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/accept", new InvitationResponseRequest { ExpectedRevision = invitation.Revision });
        Assert.Equal(HttpStatusCode.NotFound, ownerCannotAccept.StatusCode);
        As(f, invitee);
        var stale = await f.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/accept", new InvitationResponseRequest { ExpectedRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Single((await f.Client.GetFromJsonAsync<TeamInvitationSummary[]>("api/v1/me/invitations"))!);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/decline", new InvitationResponseRequest { ExpectedRevision = invitation.Revision })).StatusCode);
        Assert.Empty((await f.Client.GetFromJsonAsync<TeamInvitationSummary[]>("api/v1/me/invitations"))!);
    }

    [Fact]
    public async Task Team_OwnerCanAppointLeadAndLeadCanInviteButCannotChangeRoles()
    {
        await using var f = new ApiFixture(); await f.Start();
        var owner = await NewUser(f, "owner@example.com"); var lead = await NewUser(f, "lead@example.com"); var guest = await NewUser(f, "guest@example.com");
        As(f, owner); var team = await NewTeam(f);
        var invitation = (await (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = lead.Profile.Id })).Content.ReadFromJsonAsync<TeamInvitationSummary>())!;
        As(f, lead); await f.Client.PostAsJsonAsync($"api/v1/me/invitations/{invitation.Id}/accept", new InvitationResponseRequest { ExpectedRevision = invitation.Revision });
        As(f, owner);
        var members = await f.Client.GetFromJsonAsync<TeamMemberSummary[]>($"api/v1/teams/{team.Id}/members");
        var leadMember = Assert.Single(members!, x => x.UserId == lead.Profile.Id);
        var promotion = await f.Client.PutAsJsonAsync($"api/v1/teams/{team.Id}/members/{lead.Profile.Id}/role", new ChangeMemberRoleRequest { Role = 1, ExpectedRevision = leadMember.Revision });
        Assert.Equal(1, (await promotion.Content.ReadFromJsonAsync<TeamMemberSummary>())!.Role);
        var stale = await f.Client.PutAsJsonAsync($"api/v1/teams/{team.Id}/members/{lead.Profile.Id}/role", new ChangeMemberRoleRequest { Role = 0, ExpectedRevision = leadMember.Revision });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        As(f, lead);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync($"api/v1/teams/{team.Id}/invitations", new InviteTeamMemberRequest { RecipientUserId = guest.Profile.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.PutAsJsonAsync($"api/v1/teams/{team.Id}/members/{lead.Profile.Id}/role", new ChangeMemberRoleRequest { Role = 0, ExpectedRevision = 2 })).StatusCode);
        As(f, owner);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PutAsJsonAsync($"api/v1/teams/{team.Id}/members/{owner.Profile.Id}/role", new ChangeMemberRoleRequest { Role = 0, ExpectedRevision = 1 })).StatusCode);
    }

    [Fact]
    public async Task Friendship_RequiresRecipientAcceptanceAndHidesEmail()
    {
        await using var f = new ApiFixture(); await f.Start();
        var alice = await NewUser(f, "alice@example.com");
        var bob = await NewUser(f, "bob@example.com");
        As(f, alice);
        Assert.Equal(HttpStatusCode.Accepted, (await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = bob.Profile.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = bob.Profile.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = alice.Profile.Id })).StatusCode);
        Assert.Empty((await f.Client.GetFromJsonAsync<FriendProfile[]>("api/v1/me/friends"))!);
        As(f, bob);
        var requests = await f.Client.GetFromJsonAsync<FriendRequestSummary[]>("api/v1/me/friend-requests");
        var request = Assert.Single(requests!); Assert.True(request.Incoming); Assert.Equal(alice.Profile.Id, request.OtherUserId);
        Assert.DoesNotContain("@", request.OtherDisplayName);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/me/friend-requests/{request.Id}/accept", new InvitationResponseRequest { ExpectedRevision = request.Revision })).StatusCode);
        var friends = await f.Client.GetFromJsonAsync<FriendProfile[]>("api/v1/me/friends");
        Assert.Equal(alice.Profile.DisplayName, Assert.Single(friends!).DisplayName);
        As(f, alice);
        Assert.Equal(bob.Profile.DisplayName, Assert.Single((await f.Client.GetFromJsonAsync<FriendProfile[]>("api/v1/me/friends"))!).DisplayName);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = bob.Profile.Id })).StatusCode);
    }

    [Fact]
    public async Task Friendship_DeclinedRequestCanBeRetriedButPendingReverseRequestIsNotDuplicated()
    {
        await using var f = new ApiFixture(); await f.Start();
        var alice = await NewUser(f, "alice@example.com"); var bob = await NewUser(f, "bob@example.com");
        As(f, alice); await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = bob.Profile.Id });
        As(f, bob);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = alice.Profile.Id })).StatusCode);
        var request = Assert.Single((await f.Client.GetFromJsonAsync<FriendRequestSummary[]>("api/v1/me/friend-requests"))!);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.PostAsJsonAsync($"api/v1/me/friend-requests/{request.Id}/decline", new InvitationResponseRequest { ExpectedRevision = request.Revision })).StatusCode);
        As(f, alice);
        Assert.Equal(HttpStatusCode.Accepted, (await f.Client.PostAsJsonAsync("api/v1/me/friend-requests", new FriendRequestRequest { RecipientUserId = bob.Profile.Id })).StatusCode);
    }
}
