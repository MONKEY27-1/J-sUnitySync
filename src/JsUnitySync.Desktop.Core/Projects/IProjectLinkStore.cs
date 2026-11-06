namespace JsUnitySync.Desktop.Core.Projects;

public interface IProjectLinkStore
{
    IReadOnlyList<LocalProjectLink> Load(string serverAddress, string userId);
    void Add(LocalProjectLink link);
    void Remove(string serverAddress, string userId, string projectId);
}
