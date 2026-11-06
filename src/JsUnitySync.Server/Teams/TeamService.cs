using JsUnitySync.Contracts.Teams;
using JsUnitySync.Domain.Identity;
using JsUnitySync.Server.Accounts;
using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Teams;

public sealed class TeamService(CollaborationDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<TeamSummary>> ListTeams(string userId, CancellationToken token) =>
        await db.Memberships.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new TeamSummary { Id = x.TeamId, Name = x.Team.Name, Role = x.Role, Revision = x.Team.Revision, MemberCount = db.Memberships.Count(m => m.TeamId == x.TeamId) })
            .ToListAsync(token);

    public async Task<TeamSummary> CreateTeam(string userId, CreateTeamRequest request, CancellationToken token)
    {
        var team = new TeamRecord { Name = AccountService.Name(request.Name), CreatedById = userId, CreatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds() };
        db.Teams.Add(team);
        db.Memberships.Add(new TeamMembership { TeamId = team.Id, Team = team, UserId = userId, Role = (int)TeamRole.Owner, JoinedUnixSeconds = team.CreatedUnixSeconds });
        await db.SaveChangesAsync(token);
        return new TeamSummary { Id = team.Id, Name = team.Name, Role = (int)TeamRole.Owner, MemberCount = 1, Revision = team.Revision };
    }

    public async Task<IReadOnlyList<TeamMemberSummary>> Members(string userId, string teamId, CancellationToken token)
    {
        await RequireMember(userId, teamId, token);
        return await db.Memberships.AsNoTracking().Where(x => x.TeamId == teamId)
            .OrderBy(x => x.Role).ThenBy(x => x.User.DisplayName)
            .Select(x => new TeamMemberSummary { UserId = x.UserId, DisplayName = x.User.DisplayName, Role = x.Role, Revision = x.Revision }).ToListAsync(token);
    }

    public async Task<TeamMemberSummary> ChangeRole(string userId, string teamId, string memberId, ChangeMemberRoleRequest request, CancellationToken token)
    {
        await RequireRole(userId, teamId, TeamRole.Owner, token);
        if (request.Role != (int)TeamRole.Member && request.Role != (int)TeamRole.Lead)
            throw ApiFault.Invalid("A member may be assigned the Member or Lead role. Ownership cannot be reassigned through this action.");
        var membership = await db.Memberships.Include(x => x.User).SingleOrDefaultAsync(x => x.TeamId == teamId && x.UserId == memberId, token) ?? throw ApiFault.Missing();
        if (membership.Role == (int)TeamRole.Owner || membership.Revision != request.ExpectedRevision) throw ApiFault.Conflict();
        membership.Role = request.Role;
        membership.Revision++;
        await db.SaveChangesAsync(token);
        return new TeamMemberSummary { UserId = membership.UserId, DisplayName = membership.User.DisplayName, Role = membership.Role, Revision = membership.Revision };
    }

    public async Task<TeamInvitationSummary> Invite(string userId, string teamId, InviteTeamMemberRequest request, CancellationToken token)
    {
        await RequireRole(userId, teamId, TeamRole.Lead, token);
        if (userId == request.RecipientUserId) throw ApiFault.Invalid("You cannot invite yourself.");
        var recipient = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RecipientUserId, token);
        var team = await db.Teams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == teamId, token);
        if (recipient is null || team is null) throw ApiFault.Missing();
        if (await db.Memberships.AnyAsync(x => x.TeamId == teamId && x.UserId == recipient.Id, token))
            throw ApiFault.Conflict();
        var invitation = await db.TeamInvitations.SingleOrDefaultAsync(x => x.TeamId == teamId && x.InviteeId == recipient.Id, token);
        var now = clock.GetUtcNow();
        if (invitation is { Status: 0 } && invitation.ExpiresUnixSeconds > now.ToUnixTimeSeconds()) throw ApiFault.Conflict();
        if (invitation is null)
        {
            invitation = new TeamInvitation { TeamId = teamId, InviteeId = recipient.Id };
            db.TeamInvitations.Add(invitation);
        }
        invitation.InviterId = userId;
        invitation.Status = 0;
        invitation.ExpiresUnixSeconds = now.AddDays(7).ToUnixTimeSeconds();
        invitation.Revision++;
        await db.SaveChangesAsync(token);
        return new TeamInvitationSummary { Id = invitation.Id, TeamId = team.Id, TeamName = team.Name, InviterName = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, token)).DisplayName, ExpiresUnixSeconds = invitation.ExpiresUnixSeconds, Revision = invitation.Revision };
    }

    public async Task<IReadOnlyList<TeamInvitationSummary>> Invitations(string userId, CancellationToken token) =>
        await db.TeamInvitations.AsNoTracking().Where(x => x.InviteeId == userId && x.Status == 0 && x.ExpiresUnixSeconds > clock.GetUtcNow().ToUnixTimeSeconds())
            .OrderBy(x => x.ExpiresUnixSeconds).Select(x => new TeamInvitationSummary
            { Id = x.Id, TeamId = x.TeamId, TeamName = x.Team.Name, InviterName = db.Users.Where(u => u.Id == x.InviterId).Select(u => u.DisplayName).First(), ExpiresUnixSeconds = x.ExpiresUnixSeconds, Revision = x.Revision }).ToListAsync(token);

    public async Task Respond(string userId, string invitationId, InvitationResponseRequest request, bool accept, CancellationToken token)
    {
        var invitation = await db.TeamInvitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.InviteeId == userId, token);
        if (invitation is null) throw ApiFault.Missing();
        if (invitation.Status != 0 || invitation.Revision != request.ExpectedRevision) throw ApiFault.Conflict();
        if (invitation.ExpiresUnixSeconds <= clock.GetUtcNow().ToUnixTimeSeconds())
        {
            invitation.Status = 3;
            invitation.Revision++;
            await db.SaveChangesAsync(token);
            throw new ApiFault(410, "invitation_expired", "This invitation has expired.");
        }
        if (accept)
            db.Memberships.Add(new TeamMembership { TeamId = invitation.TeamId, UserId = userId, Role = (int)TeamRole.Member, JoinedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds() });
        invitation.Status = accept ? 1 : 2;
        invitation.Revision++;
        await db.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<FriendProfile>> Friends(string userId, CancellationToken token)
    {
        var rows = await db.Friendships.AsNoTracking().Where(x => x.Status == 1 && (x.UserLowId == userId || x.UserHighId == userId)).ToListAsync(token);
        var ids = rows.Select(x => x.UserLowId == userId ? x.UserHighId : x.UserLowId).ToArray();
        return await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.DisplayName)
            .Select(x => new FriendProfile { UserId = x.Id, DisplayName = x.DisplayName }).ToListAsync(token);
    }

    public async Task<IReadOnlyList<FriendRequestSummary>> FriendRequests(string userId, CancellationToken token)
    {
        var rows = await db.Friendships.AsNoTracking().Where(x => x.Status == 0 && (x.UserLowId == userId || x.UserHighId == userId)).ToListAsync(token);
        var ids = rows.Select(x => x.RequesterId == userId ? (x.UserLowId == userId ? x.UserHighId : x.UserLowId) : x.RequesterId).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, token);
        return rows.Select(x =>
        {
            var other = x.RequesterId == userId ? (x.UserLowId == userId ? x.UserHighId : x.UserLowId) : x.RequesterId;
            return new FriendRequestSummary { Id = x.Id, OtherUserId = other, OtherDisplayName = names[other], Incoming = x.RequesterId != userId, Revision = x.Revision };
        }).ToArray();
    }

    public async Task RequestFriend(string userId, FriendRequestRequest request, CancellationToken token)
    {
        if (userId == request.RecipientUserId) throw ApiFault.Invalid("You cannot add yourself as a friend.");
        if (!await db.Users.AnyAsync(x => x.Id == request.RecipientUserId, token)) throw ApiFault.Missing();
        var low = string.CompareOrdinal(userId, request.RecipientUserId) < 0 ? userId : request.RecipientUserId;
        var high = low == userId ? request.RecipientUserId : userId;
        var friendship = await db.Friendships.SingleOrDefaultAsync(x => x.UserLowId == low && x.UserHighId == high, token);
        if (friendship is { Status: 1 } || friendship is { Status: 0 }) throw ApiFault.Conflict();
        if (friendship is null)
        {
            friendship = new FriendshipRecord { UserLowId = low, UserHighId = high };
            db.Friendships.Add(friendship);
        }
        friendship.RequesterId = userId;
        friendship.Status = 0;
        friendship.Revision++;
        friendship.UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(token);
    }

    public async Task RespondFriend(string userId, string requestId, InvitationResponseRequest request, bool accept, CancellationToken token)
    {
        var friendship = await db.Friendships.SingleOrDefaultAsync(x => x.Id == requestId && (x.UserLowId == userId || x.UserHighId == userId), token);
        if (friendship is null) throw ApiFault.Missing();
        if (friendship.RequesterId == userId || friendship.Status != 0 || friendship.Revision != request.ExpectedRevision) throw ApiFault.Conflict();
        friendship.Status = accept ? 1 : 2;
        friendship.Revision++;
        friendship.UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(token);
    }

    private async Task RequireMember(string userId, string teamId, CancellationToken token)
    {
        if (!await db.Memberships.AnyAsync(x => x.TeamId == teamId && x.UserId == userId, token)) throw ApiFault.Missing();
    }
    public async Task RequireRole(string userId, string teamId, TeamRole minimum, CancellationToken token)
    {
        var role = await db.Memberships.Where(x => x.TeamId == teamId && x.UserId == userId).Select(x => (int?)x.Role).SingleOrDefaultAsync(token);
        if (role is null || role < (int)minimum) throw ApiFault.Missing();
    }
}
