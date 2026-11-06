using System;
using System.Collections.Generic;

namespace JsUnitySync.Contracts.Realtime;

public sealed class CreateSessionRequest { public string Name { get; set; } = ""; }
public sealed class SessionSummary
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Name { get; set; } = "";
    public string SceneGuid { get; set; } = "";
    public string SceneManifestHash { get; set; } = "";
    public string CreatedById { get; set; } = "";
    public long CreatedUnixSeconds { get; set; }
    public long LastSequence { get; set; }
}
public sealed class SessionWelcomePayload
{
    public string ProtocolVersion { get; set; } = "";
    public string SceneGuid { get; set; } = "";
    public string SceneManifestHash { get; set; } = "";
    public long LastSequence { get; set; }
    public long ReplayFromSequence { get; set; }
}

/// <summary>JSON payload of a SceneEvent frame. Property writes are revision-checked before acceptance.</summary>
public sealed class SceneMutation
{
    public int Kind { get; set; }
    /// <summary>Stable Unity object id; empty only for scene-level events.</summary>
    public string ObjectId { get; set; } = "";
    /// <summary>Unity scene asset GUID bound to this collaboration session.</summary>
    public string SceneGuid { get; set; } = "";
    public string ComponentType { get; set; } = "";
    public string PropertyName { get; set; } = "";
    /// <summary>Unity's serialized property value. Empty for non-property mutations.</summary>
    public string SerializedValue { get; set; } = "";
    /// <summary>Required for PropertyChanged; optimistic revision observed by the writer.</summary>
    public long BaseRevision { get; set; } = -1;
    /// <summary>Typed event details (transform, name, etc) as JSON; never treated as executable code.</summary>
    public string DataJson { get; set; } = "{}";
}

public sealed class SceneConflictPayload
{
    public string ObjectId { get; set; } = "";
    public string ComponentType { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public long AcceptedRevision { get; set; }
    public string AcceptedValue { get; set; } = "";
    public string RejectedValue { get; set; } = "";
    public string AcceptedById { get; set; } = "";
    public string RejectedById { get; set; } = "";
    public string RejectedEventId { get; set; } = "";
}

public sealed class EventBatchPayload
{
    public List<string> EventIds { get; set; } = new();
}
