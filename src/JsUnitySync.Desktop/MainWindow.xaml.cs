using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using JsUnitySync.Desktop.Core.ViewModels;

namespace JsUnitySync.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Native chrome preserves resize, snap layouts, system menu and screen-reader support.
        // Attribute 20 is supported on Windows 10 20H1+; older systems keep native chrome.
        var enabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
