namespace JsUnitySync.Desktop.Core.Settings;

public interface ISettingsStore
{
    DesktopSettings Load();
    void Save(DesktopSettings settings);
}
