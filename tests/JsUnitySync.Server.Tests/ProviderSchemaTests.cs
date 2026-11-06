using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JsUnitySync.Server.Tests;

public sealed class ProviderSchemaTests
{
    [Fact]
    public void PostgresProvider_GeneratesPostgresMigrationSqlWithoutConnectingToServer()
    {
        var options = new DbContextOptionsBuilder<CollaborationDbContext>()
            .UseNpgsql("Host=localhost;Database=jsunitysync;Username=migration-script")
            .Options;
        using var db = new CollaborationDbContext(options);
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("BIGINT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BOOLEAN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SessionEvents", sql, StringComparison.Ordinal);
        Assert.Contains("SessionProperties", sql, StringComparison.Ordinal);
        Assert.Contains("CollaborationSessions", sql, StringComparison.Ordinal);
        Assert.Contains("\"Status\" = 0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOINCREMENT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA", sql, StringComparison.OrdinalIgnoreCase);
    }
}
