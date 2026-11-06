using System.Text.Json;
using JsUnitySync.Desktop.Core.Settings;

namespace JsUnitySync.Desktop.Core.Projects;

/// <summary>Stores only locally selected folder associations. Never opens paths for write.</summary>
public sealed class JsonProjectLinkStore(string path) : IProjectLinkStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<LocalProjectLink> Load(string serverAddress, string userId)
    {
        lock (Gate)
        {
            var all = ReadAll();
            return all.Where(x => x.ServerAddress == serverAddress && x.UserId == userId).ToArray();
        }
    }

    public void Add(LocalProjectLink link)
    {
        if (!ServerEndpoint.TryParse(link.ServerAddress, out var server) || server.AbsoluteUri != link.ServerAddress ||
            !ValidId(link.UserId) || !ValidId(link.TeamId) || !ValidId(link.ProjectId))
            throw new InvalidDataException("The local project link is invalid or the Unity folder is no longer available.");
        if (!UnityProjectFolderValidator.TryValidate(link.LocalPath, out var folder, out _))
            throw new InvalidDataException("The local project link is invalid or the Unity folder is no longer available.");
        var normalized = link with { LocalPath = folder.FullPath };
        lock (Gate)
        {
            var all = ReadAll();
            if (all.Count >= 2000) throw new InvalidOperationException("This device has reached the safe limit of 2,000 local project links.");
            if (all.Any(x => x.ServerAddress == link.ServerAddress && x.UserId == link.UserId &&
                (x.ProjectId == link.ProjectId || string.Equals(x.LocalPath, normalized.LocalPath, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("This project or local folder is already linked. Refresh before retrying.");
            all.Add(normalized);
            WriteAll(all);
        }
    }

    public void Remove(string serverAddress, string userId, string projectId)
    {
        lock (Gate)
        {
            var all = ReadAll();
            all.RemoveAll(x => x.ServerAddress == serverAddress && x.UserId == userId && x.ProjectId == projectId);
            WriteAll(all);
        }
    }

    private static bool ValidId(string value) => value is { Length: 26 } && value.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c));

    private List<LocalProjectLink> ReadAll()
    {
        if (!File.Exists(path)) return [];
        var values = JsonSerializer.Deserialize<List<LocalProjectLink>>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Local project links are invalid; original file is preserved.");
        if (values.Count > 2000) throw new InvalidDataException("Local project link file exceeds the safe entry limit.");
        return values;
    }

    private void WriteAll(List<LocalProjectLink> values)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(values, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
