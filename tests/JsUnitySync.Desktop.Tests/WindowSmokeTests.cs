using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JsUnitySync.Desktop.Tests;

public sealed class WindowSmokeTests
{
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message)) Messages.Add(message);
        }
    }

    [Fact]
    public async Task Window_LoadsThemeBindsEveryPageAndRendersAtMinimumAndDefaultSizes()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            var errors = new BindingErrors();
            var source = PresentationTraceSources.DataBindingSource;
            var previousLevel = source.Switch.Level;
            source.Switch.Level = SourceLevels.Error;
            source.Listeners.Add(errors);
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                using var vm = ShellViewModelTests.Create();
                window = new MainWindow(vm) { Left = -10000, Top = -10000, ShowInTaskbar = false };
                window.Show();
                Pump();
                Assert.Equal(Color.FromRgb(12, 13, 18), ((SolidColorBrush)window.Background).Color);
                Assert.NotNull(window.FindResource("PrimaryButton"));
                var navigation = (ListBox)window.FindName("NavigationList");
                Assert.Equal(6, navigation.Items.Count);
                foreach (var size in new[] { (1320d, 880d), (1020d, 680d) })
                {
                    window.Width = size.Item1;
                    window.Height = size.Item2;
                    foreach (var page in vm.Pages)
                    {
                        navigation.SelectedItem = page;
                        Pump();
                        Assert.Same(page, vm.SelectedPage);
                        Assert.Equal(vm.IsOverview ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("OverviewPanel")).Visibility);
                        Assert.Equal(vm.IsSettings ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("SettingsPanel")).Visibility);
                        Assert.Equal(vm.IsWorkspace ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("WorkspacePanel")).Visibility);
                        Assert.Equal(vm.IsSessions ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("SessionPanel")).Visibility);
                        Assert.Equal(vm.IsTeam ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("TeamPanel")).Visibility);
                        Assert.Equal(vm.IsProjects ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)window.FindName("ProjectPanel")).Visibility);
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        Assert.True(bitmap.PixelWidth > 0);
                    }
                }
                vm.OpenSettingsCommand.Execute(null);
                Pump();
                var address = (TextBox)window.FindName("ServerAddressBox");
                var addressBinding = address.GetBindingExpression(TextBox.TextProperty);
                Assert.True(addressBinding is not null, string.Join(Environment.NewLine, errors.Messages));
                address.SetCurrentValue(TextBox.TextProperty, "https://sync.example.com");
                addressBinding.UpdateSource();
                ((CheckBox)window.FindName("ReduceMotionCheckBox")).SetCurrentValue(CheckBox.IsCheckedProperty, true);
                vm.SaveSettingsCommand.Execute(null);
                Assert.Equal("https://sync.example.com/", vm.SavedServerAddress);
                Assert.True(vm.ReduceMotion);
                Pump();
                Assert.Empty(errors.Messages);

                // Optional local visual review; not a generated deliverable or required by the suite.
                var capture = Environment.GetEnvironmentVariable("JSUNITYSYNC_TEST_SCREENSHOT");
                if (!string.IsNullOrEmpty(capture))
                {
                    window.Width = 1320;
                    window.Height = 880;
                    vm.Pages[0].NavigateCommand.Execute(null);
                    Pump();
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = System.IO.File.Create(capture);
                    encoder.Save(output);
                }
            }
            catch (Exception error) { completed.TrySetException(error); }
            finally
            {
                window?.Close();
                source.Listeners.Remove(errors);
                source.Switch.Level = previousLevel;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            completed.TrySetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
