using System.IO;
using System.Net.Http;
using System.Windows;
using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Projects;
using JsUnitySync.Desktop.Core.Teams;
using JsUnitySync.Desktop.Core.Realtime;
using JsUnitySync.Desktop.Core.Sessions;
using JsUnitySync.Desktop.Services;
using JsUnitySync.Desktop.Core.Settings;
using JsUnitySync.Desktop.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace JsUnitySync.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private LocalBackendHost? _localBackend;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsUnitySync");
            var logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(dataDirectory, "logs", "desktop-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true)
                .CreateLogger();

            Log.Logger = logger;
            _localBackend = new LocalBackendHost();
            await _localBackend.StartAsync();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddSerilog(logger, dispose: true));
            services.AddSingleton<ISettingsStore>(new JsonSettingsStore(Path.Combine(dataDirectory, "desktop-settings.json")));
            // A health probe must not follow redirects to another origin or downgrade HTTPS.
            services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = 64 * 1024
            });
            services.AddSingleton<ISessionStore>(new ProtectedSessionStore(Path.Combine(dataDirectory, "session.dpapi")));
            services.AddSingleton<IRealtimeClientStore>(new ProtectedRealtimeClientStore(Path.Combine(dataDirectory, "realtime-inbox.dpapi")));
            services.AddSingleton<RealtimeSessionClient>();
            services.AddSingleton<CollaborationClient>();
            services.AddSingleton<AccountViewModel>();
            services.AddSingleton<TeamWorkspaceViewModel>();
            services.AddSingleton<IProjectLinkStore>(new JsonProjectLinkStore(Path.Combine(dataDirectory, "project-links.json")));
            services.AddSingleton<ProjectWorkspaceViewModel>();
            services.AddSingleton<SessionWorkspaceViewModel>();
            services.AddSingleton<IServerHealthClient, ServerHealthClient>();
            services.AddSingleton<ShellViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var shell = _services.GetRequiredService<ShellViewModel>();
            shell.LoadSettings();
            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Loaded += async (_, _) =>
            {
                await Task.WhenAll(shell.CheckConnectionCommand.ExecuteAsync(), shell.Account.RestoreAsync());
            };
            MainWindow.Closed += (_, _) => shell.Dispose();
            MainWindow.Show();
            _services.GetRequiredService<ILogger<App>>().LogInformation("Desktop shell started");
        }
        catch (Exception error)
        {
            try { _localBackend?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _services?.Dispose();
            Log.Error(error, "J'sUnitySync startup failed");
            MessageBox.Show("J'sUnitySync could not start its local collaboration service. Check that port 5080 is available and LocalAppData is writable.",
                "J'sUnitySync startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _localBackend?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally
        {
            _services?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
