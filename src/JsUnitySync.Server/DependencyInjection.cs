using System.Threading.RateLimiting;
using JsUnitySync.Server.Accounts;
using JsUnitySync.Server.Assets;
using JsUnitySync.Server.Persistence;
using JsUnitySync.Server.Teams;
using JsUnitySync.Server.Projects;
using JsUnitySync.Server.Realtime;
using JsUnitySync.Server.Sessions;
using JsUnitySync.Server.Scripting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Npgsql;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JsUnitySync.Server;

public static class DependencyInjection
{
    public static IServiceCollection AddCollaborationServer(this IServiceCollection services, IConfiguration configuration)
    {
        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var isProduction = environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase);
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connection = configuration["Database:ConnectionString"];
        services.AddSingleton(new DatabaseProviderInfo(provider));
        services.AddDbContextFactory<CollaborationDbContext>(options =>
        {
            switch (provider.ToLowerInvariant())
            {
                case "sqlite":
                    if (string.IsNullOrWhiteSpace(connection))
                    {
                        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsUnitySync", "server");
                        Directory.CreateDirectory(directory);
                        connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "collaboration.db"), ForeignKeys = true, DefaultTimeout = 15 }.ToString();
                    }
                    options.UseSqlite(connection, sqlite => sqlite.CommandTimeout(30));
                    break;
                case "postgres":
                case "postgresql":
                    if (string.IsNullOrWhiteSpace(connection))
                        throw new InvalidOperationException("Database:ConnectionString is required when Database:Provider is Postgres. Supply it via a secret-backed environment variable.");
                    var postgres = new NpgsqlConnectionStringBuilder(connection);
                    postgres.Timeout = Math.Clamp(postgres.Timeout, 3, 30);
                    postgres.CommandTimeout = 30;
                    postgres.Pooling = true;
                    postgres.MaxPoolSize = Math.Clamp(postgres.MaxPoolSize, 10, 100);
                    var host = postgres.Host ?? "";
                    if (isProduction &&
                        !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                        host != "127.0.0.1" && host != "::1" && postgres.SslMode != SslMode.VerifyFull)
                        throw new InvalidOperationException("Production PostgreSQL connections to remote hosts must use SSL Mode=VerifyFull.");
                    options.UseNpgsql(postgres.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
                    break;
                default:
                    throw new InvalidOperationException("Database:Provider must be Sqlite or Postgres.");
            }
        });
        services.AddSingleton(TimeProvider.System);
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
        services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddScoped<AccountService>();
        services.AddScoped<TeamService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<CollaborationSessionService>();
        services.AddScoped<RealtimeEventService>();
        services.AddScoped<ScriptDocumentService>();
        services.AddScoped<ProjectAssetService>();
        services.AddScoped<ProjectHistoryService>();
        services.AddScoped<RealtimeSocketService>();
        var redisConnection = configuration["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnection))
            services.AddSingleton<IRealtimeBackplane, InMemoryRealtimeBackplane>();
        else
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnection);
                redisOptions.AbortOnConnectFail = false;
                redisOptions.ConnectRetry = 3;
                redisOptions.ConnectTimeout = 3_000;
                if (isProduction && !redisOptions.Ssl)
                    throw new InvalidOperationException("Production Redis connections must enable TLS (ssl=true).");
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            services.AddSingleton<IRealtimeBackplane, RedisRealtimeBackplane>();
        }
        services.AddAuthentication("SessionBearer").AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>("SessionBearer", _ => { });
        services.AddAuthorization();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("realtime", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("script-write", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("asset-upload", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}
