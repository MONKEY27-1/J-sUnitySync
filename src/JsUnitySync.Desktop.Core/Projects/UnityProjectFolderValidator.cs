namespace JsUnitySync.Desktop.Core.Projects;

public sealed record UnityProjectFolder(string FullPath, string FolderName);

/// <summary>Read-only structural validation. No file contents are read or written.</summary>
public static class UnityProjectFolderValidator
{
    public static bool TryValidate(string? selectedPath, out UnityProjectFolder project, out string error)
    {
        project = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(selectedPath)) { error = "Choose a Unity project folder."; return false; }
        string full;
        try { full = Path.GetFullPath(selectedPath.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { error = "The selected path is invalid or inaccessible."; return false; }
        try
        {
            var root = new DirectoryInfo(full);
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            { error = "Choose an existing local Unity project folder, not a link or shortcut."; return false; }
            var assets = new DirectoryInfo(Path.Combine(full, "Assets"));
            var settings = new DirectoryInfo(Path.Combine(full, "ProjectSettings"));
            var version = new FileInfo(Path.Combine(settings.FullName, "ProjectVersion.txt"));
            if (!assets.Exists || !settings.Exists || !version.Exists || version.Length > 64 * 1024 ||
                (assets.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (settings.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (version.Attributes & FileAttributes.ReparsePoint) != 0)
            { error = "This folder does not have the standard Unity project structure (Assets and ProjectSettings/ProjectVersion.txt)."; return false; }
            project = new UnityProjectFolder(root.FullName, root.Name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error = "The selected Unity folder cannot be safely inspected."; return false; }
    }
}
