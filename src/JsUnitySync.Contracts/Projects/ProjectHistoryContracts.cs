namespace JsUnitySync.Contracts.Projects;

public sealed class ProjectFileVersionSummary
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public long Revision { get; set; }
    public string ContentHash { get; set; } = "";
    public long Length { get; set; }
    public string SavedById { get; set; } = "";
    public long SavedUnixSeconds { get; set; }
}

public sealed class RestoreProjectFileVersionRequest
{
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public long SourceRevision { get; set; }
    /// <summary>Current revision observed by the requester; restore always creates a new revision.</summary>
    public long ExpectedCurrentRevision { get; set; }
}
