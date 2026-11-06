using JsUnitySync.Server;
using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JsUnitySync.Server.Tests;

public sealed class DatabaseProviderTests
{
    [Fact]
    public void ConfiguredPostgresProviderBuildsAContextWithoutConnecting()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Postgres",
            ["Database:ConnectionString"] = "Host=localhost;Database=jsunitysync;Username=migration-script;Pooling=false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCollaborationServer(config);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        Assert.Equal("Postgres", scope.ServiceProvider.GetRequiredService<DatabaseProviderInfo>().Name);
    }

    [Fact]
    public void PostgresProviderRequiresAnExplicitConnectionString()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Database:Provider"] = "Postgres" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCollaborationServer(config);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<CollaborationDbContext>());
    }
}
