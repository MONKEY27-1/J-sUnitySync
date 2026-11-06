using System.Text.Json;

namespace JsUnitySync.Desktop.Core.Settings;

/// <summary>Owns only its settings file. Atomic replacement preserves previous settings on write failure.</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public DesktopSettings Load()
    {
        if (!File.Exists(path)) return new DesktopSettings();
        var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Settings are empty.");
        Validate(settings);
        return settings;
    }

    public void Save(DesktopSettings settings)
    {
        Validate(settings);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(DesktopSettings settings)
    {
        if (!ServerEndpoint.TryParse(settings.ServerAddress, out _))
            throw new InvalidDataException(ServerEndpoint.ValidationMessage);
    }
}
