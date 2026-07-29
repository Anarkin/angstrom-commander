using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AngstromCommander.Server.Tests.Integration;

internal sealed class ServerFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Database", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
    }
}
