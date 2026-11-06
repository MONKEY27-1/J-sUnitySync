using JsUnitySync.Desktop.Core.Mvvm;

namespace JsUnitySync.Desktop.Core.ViewModels;

public sealed class NavigationPage(string key, string title, string glyph, string eyebrow, string description,
    string emptyTitle, string emptyDescription, string availability)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Eyebrow { get; } = eyebrow;
    public string Description { get; } = description;
    public string EmptyTitle { get; } = emptyTitle;
    public string EmptyDescription { get; } = emptyDescription;
    public string Availability { get; } = availability;
    public RelayCommand NavigateCommand { get; internal set; } = null!;
}
