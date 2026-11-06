using JsUnitySync.Domain.Ids;

namespace JsUnitySync.Server.Persistence;

public sealed class CollaborationSessionRecord
{
    public string Id { get; set; } = StableId.NewId().ToString();
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Unity AssetDatabase GUID of the shared scene; empty until the first Editor client binds it.</summary>
    public string SceneGuid { get; set; } = "";
    public string SceneManifestHash { get; set; } = "";
    public string CreatedById { get; set; } = "";
    public long CreatedUnixSeconds { get; set; }
    public long LastSequence { get; set; }
    public long Revision { get; set; }
    public bool IsClosed { get; set; }
    public ProjectRecord Project { get; set; } = null!;
}

public sealed class SessionEventRecord
{
    public string SessionId { get; set; } = "";
    public long Sequence { get; set; }
    public string EventId { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public int Kind { get; set; }
    public string ObjectId { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public long UtcTimestampMilliseconds { get; set; }
    public CollaborationSessionRecord Session { get; set; } = null!;
}

public sealed class SessionPropertyRecord
{
    public string SessionId { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string ComponentType { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public long Revision { get; set; }
    public string AcceptedValue { get; set; } = "";
    public string AcceptedById { get; set; } = "";
}

public sealed class SessionConflictRecord
{
    public string Id { get; set; } = StableId.NewId().ToString();
    public string SessionId { get; set; } = "";
    public string RejectedEventId { get; set; } = "";
    public string AcceptedById { get; set; } = "";
    public string RejectedById { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string ComponentType { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public long AcceptedRevision { get; set; }
    public string AcceptedValue { get; set; } = "";
    public string RejectedValue { get; set; } = "";
    public long RelatedSequence { get; set; }
    public long CreatedUnixSeconds { get; set; }
    public CollaborationSessionRecord Session { get; set; } = null!;
}

public sealed class SessionClientCursorRecord
{
    public string SessionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public long LastAckedSequence { get; set; } = -1;
    public long UpdatedUnixSeconds { get; set; }
    public CollaborationSessionRecord Session { get; set; } = null!;
}
