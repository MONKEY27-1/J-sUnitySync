namespace JsUnitySync.Desktop.Core.Projects;

/// <summary>Device-local association. LocalPath is never part of an API contract.</summary>
public sealed record LocalProjectLink(string ServerAddress, string UserId, string TeamId, string ProjectId, string ProjectName, string LocalPath);
