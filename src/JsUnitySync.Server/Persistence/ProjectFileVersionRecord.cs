using JsUnitySync.Domain.Ids;

namespace JsUnitySync.Server.Persistence;

public sealed class ProjectFileVersionRecord
{
    public string Id { get; set; } = StableId.NewId().ToString();
    public string ProjectId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public long Revision { get; set; }
    /// <summary>Populated only for bounded collaborative C# documents.</summary>
    public string Content { get; set; } = "";
    /// <summary>Content-addressed blob hash for synchronized binary assets.</summary>
    public string ContentHash { get; set; } = "";
    public long Length { get; set; }
    public string SavedById { get; set; } = "";
    public long SavedUnixSeconds { get; set; }
    public ProjectRecord Project { get; set; } = null!;
}
