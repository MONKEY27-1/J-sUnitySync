namespace JsUnitySync.Contracts.Teams;

public sealed class CreateTeamRequest { public string Name { get; set; } = ""; }
public sealed class TeamSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Role { get; set; }
    public int MemberCount { get; set; }
    public string RoleName => Role switch { 2 => "Owner", 1 => "Lead", _ => "Member" };
    public long Revision { get; set; }
}
public sealed class TeamMemberSummary
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Role { get; set; }
    public string RoleName => Role switch { 2 => "Owner", 1 => "Lead", _ => "Member" };
    public long Revision { get; set; }
}
public sealed class ChangeMemberRoleRequest { public int Role { get; set; } public long ExpectedRevision { get; set; } }
public sealed class InviteTeamMemberRequest { public string RecipientUserId { get; set; } = ""; }
public sealed class TeamInvitationSummary
{
    public string Id { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string InviterName { get; set; } = "";
    public long ExpiresUnixSeconds { get; set; }
    public long Revision { get; set; }
}
public sealed class InvitationResponseRequest { public long ExpectedRevision { get; set; } }
public sealed class FriendRequestRequest { public string RecipientUserId { get; set; } = ""; }
public sealed class FriendProfile { public string UserId { get; set; } = ""; public string DisplayName { get; set; } = ""; }
public sealed class FriendRequestSummary
{
    public string Id { get; set; } = "";
    public string OtherUserId { get; set; } = "";
    public string OtherDisplayName { get; set; } = "";
    public bool Incoming { get; set; }
    public string Direction => Incoming ? "Received" : "Sent";
    public long Revision { get; set; }
}
