using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Data;

/// <summary>
/// Applies pending migrations at startup so fresh environments self-initialize their schema.
/// Opt-in per environment (<c>Database:MigrateOnStartup</c>) and off by default, because build-time
/// tooling starts the app to inspect it — OpenAPI document generation must never touch a database.
/// </summary>
internal sealed class DatabaseMigrator(IServiceProvider services, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Database:MigrateOnStartup", defaultValue: false))
        {
            return;
        }

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
