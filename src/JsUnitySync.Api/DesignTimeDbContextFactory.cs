using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace JsUnitySync.Api;

/// <summary>Schema tooling never starts the host or touches a user's runtime database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CollaborationDbContext>
{
    public CollaborationDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("Database__Provider") ?? "Sqlite";
        var connection = Environment.GetEnvironmentVariable("Database__ConnectionString");
        var options = new DbContextOptionsBuilder<CollaborationDbContext>();
        switch (provider.ToLowerInvariant())
        {
            case "sqlite":
                options.UseSqlite(connection ?? "Data Source=:memory:");
                break;
            case "postgres":
            case "postgresql":
                options.UseNpgsql(connection ?? new NpgsqlConnectionStringBuilder
                { Host = "localhost", Database = "jsunitysync", Username = "migration-script" }.ConnectionString);
                break;
            default:
                throw new InvalidOperationException("Database__Provider must be Sqlite or Postgres.");
        }
        return new CollaborationDbContext(options.Options);
    }
}
