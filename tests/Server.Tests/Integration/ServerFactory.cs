using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AngstromCommander.Server.Tests.Integration;

internal sealed class ServerFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Tests bring their own key rather than leaning on any environment's default.</summary>
    public const string SigningKey = "test-signing-key-0123456789abcdef-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Database", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:JwtSigningKey", SigningKey);
    }
}
