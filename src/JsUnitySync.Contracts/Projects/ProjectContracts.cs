namespace JsUnitySync.Contracts.Projects;

public sealed class CreateProjectRequest { public string Name { get; set; } = ""; }
public sealed class UpdateProjectRequest { public string Name { get; set; } = ""; public long ExpectedRevision { get; set; } }
public sealed class ArchiveProjectRequest { public long ExpectedRevision { get; set; } }
public sealed class ProjectSummary
{
    public string Id { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedById { get; set; } = "";
    public long Revision { get; set; }
    public bool IsArchived { get; set; }
    public long CreatedUnixSeconds { get; set; }
}
