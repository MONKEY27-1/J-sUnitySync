using JsUnitySync.Domain.Scenes;
using JsUnitySync.Domain.Sync;
using Serilog;

namespace JsUnitySync.Api;

public static class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false)
                    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
                    .Build())
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "jsunitysync-api-.log"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("J'sUnitySync API starting");
            CreateHost(args).Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "J'sUnitySync API terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static WebApplication CreateHost(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog();

        // Domain services (grows per phase via each library's AddXxx extension).
        builder.Services.AddSingleton<SessionSequencer>();

        var app = builder.Build();

        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", service = "JsUnitySync.Api" }));
        app.MapGet("/health/ready", (SessionSequencer sequencer) =>
            Results.Ok(new { status = "ready", service = "JsUnitySync.Api", lastSequence = sequencer.LastSequence }));

        return app;
    }
}
