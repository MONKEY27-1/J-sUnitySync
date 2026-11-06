using System.Net;
using JsUnitySync.Server;
using JsUnitySync.Server.Accounts;
using JsUnitySync.Server.Assets;
using JsUnitySync.Server.Persistence;
using JsUnitySync.Server.Teams;
using JsUnitySync.Server.Sessions;
using JsUnitySync.Server.Realtime;
using JsUnitySync.Server.Scripting;
using Microsoft.AspNetCore.WebSockets;
using JsUnitySync.Server.Projects;
using Microsoft.EntityFrameworkCore;
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

    public static WebApplication CreateHost(string[] args, TimeSpan? shutdownTimeout = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (shutdownTimeout is { } timeout)
            builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = timeout);

        builder.Host.UseSerilog();

        // Domain services (grows per phase via each library's AddXxx extension).
        builder.Services.AddCollaborationServer(builder.Configuration);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
            var provider = scope.ServiceProvider.GetRequiredService<DatabaseProviderInfo>();
            var applyMigrations = builder.Configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup") ??
                provider.Name.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
            if (applyMigrations)
                database.Database.Migrate();
            else if (database.Database.GetPendingMigrations().Any())
                throw new InvalidOperationException("Database has pending migrations. Apply the checked-in idempotent migration script before starting with Database:ApplyMigrationsOnStartup=false.");
        }

        app.UseMiddleware<ApiFaultMiddleware>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/realtime"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { code = "https_required", message = "HTTPS is required outside loopback development." });
                    return;
                }
            }
            await next();
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAccountEndpoints();
        app.MapTeamEndpoints();
        app.MapProjectEndpoints();
        app.MapSessionEndpoints();
        app.MapScriptEndpoints();
        app.MapProjectAssetEndpoints();
        app.MapProjectHistoryEndpoints();
        app.MapGet("/realtime/v1/sessions/{sessionId}", (HttpContext context, string sessionId, RealtimeSocketService realtime) =>
            realtime.HandleAsync(context, sessionId)).RequireAuthorization().RequireRateLimiting("realtime");

        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", service = "JsUnitySync.Api" }));
        app.MapGet("/health/ready", async (DatabaseProviderInfo provider, CollaborationDbContext database, IRealtimeBackplane backplane, CancellationToken token) =>
        {
            var databaseReady = await database.Database.CanConnectAsync(token);
            if (!databaseReady || !backplane.IsHealthy)
                return Results.Json(new { status = "unavailable", service = "JsUnitySync.Api", database = provider.Name, realtimeBackplane = backplane.IsHealthy ? "connected" : "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new { status = "ready", service = "JsUnitySync.Api", database = provider.Name, realtimeBackplane = backplane.GetType().Name });
        });

        return app;
    }
}
