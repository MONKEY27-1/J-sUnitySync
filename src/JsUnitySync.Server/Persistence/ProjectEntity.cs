namespace JsUnitySync.Server.Persistence;

public sealed class ProjectRecord
{
    public string Id { get; set; } = JsUnitySync.Domain.Ids.StableId.NewId().ToString();
    public string TeamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string CreatedById { get; set; } = "";
    public long Revision { get; set; } = 1;
    public long AssetCatalogRevision { get; set; }
    public bool IsArchived { get; set; }
    public long CreatedUnixSeconds { get; set; }
    public TeamRecord Team { get; set; } = null!;
}
