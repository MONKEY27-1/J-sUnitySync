namespace JsUnitySync.Contracts.Projects;

public sealed class ProjectAssetSummary
{
    public string ProjectId { get; set; } = "";
    public string Path { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long Length { get; set; }
    public long Revision { get; set; }
    public string UpdatedById { get; set; } = "";
    public long UpdatedUnixSeconds { get; set; }
}
