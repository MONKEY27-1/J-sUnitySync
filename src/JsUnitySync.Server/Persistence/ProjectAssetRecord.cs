namespace JsUnitySync.Server.Persistence;

public sealed class ProjectAssetRecord
{
    public string ProjectId { get; set; } = "";
    public string Path { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long Length { get; set; }
    public long Revision { get; set; } = 1;
    public string UpdatedById { get; set; } = "";
    public long UpdatedUnixSeconds { get; set; }
    public ProjectRecord Project { get; set; } = null!;
}
