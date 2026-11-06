using System.Windows;
using System.Windows.Controls;
using JsUnitySync.Desktop.Core.Projects;
using Microsoft.Win32;

namespace JsUnitySync.Desktop.Views;

public partial class ProjectPanel : UserControl
{
    public ProjectPanel() => InitializeComponent();
    private async void ArchiveProjectClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectWorkspaceViewModel projects || projects.SelectedProject is null) return;
        var answer = MessageBox.Show(
            $"Archive '{projects.SelectedProject.Name}' for this team? Team members will no longer see it in the active project list. Local Unity folders and files will not be changed. You can restore the record later.",
            "Archive team project", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await projects.ArchiveCommand.ExecuteAsync();
    }

    private async void ChooseFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectWorkspaceViewModel projects) return;
        var dialog = new OpenFolderDialog { Title = "Choose the existing Unity project folder (read-only link)", Multiselect = false, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await projects.LinkFolderAsync(dialog.FolderName);
    }
}
