namespace JsUnitySync.Server.Persistence;

public sealed class TeamRecord
{
    public string Id { get; set; } = JsUnitySync.Domain.Ids.StableId.NewId().ToString();
    public string Name { get; set; } = "";
    public string CreatedById { get; set; } = "";
    public long Revision { get; set; } = 1;
    public long CreatedUnixSeconds { get; set; }
}

public sealed class TeamMembership
{
    public string TeamId { get; set; } = "";
    public string UserId { get; set; } = "";
    public int Role { get; set; }
    public long Revision { get; set; } = 1;
    public long JoinedUnixSeconds { get; set; }
    public TeamRecord Team { get; set; } = null!;
    public UserAccount User { get; set; } = null!;
}

public sealed class TeamInvitation
{
    public string Id { get; set; } = JsUnitySync.Domain.Ids.StableId.NewId().ToString();
    public string TeamId { get; set; } = "";
    public string InviterId { get; set; } = "";
    public string InviteeId { get; set; } = "";
    public int Status { get; set; }
    public long ExpiresUnixSeconds { get; set; }
    public long Revision { get; set; } = 1;
    public TeamRecord Team { get; set; } = null!;
}

public sealed class FriendshipRecord
{
    public string Id { get; set; } = JsUnitySync.Domain.Ids.StableId.NewId().ToString();
    public string UserLowId { get; set; } = "";
    public string UserHighId { get; set; } = "";
    public string RequesterId { get; set; } = "";
    public int Status { get; set; }
    public long Revision { get; set; } = 1;
    public long UpdatedUnixSeconds { get; set; }
}
