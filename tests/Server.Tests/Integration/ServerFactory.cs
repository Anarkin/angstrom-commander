using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AngstromCommander.Server.Tests.Integration;

internal sealed class ServerFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Tests bring their own key rather than leaning on any environment's default.</summary>
    public const string SigningKey = "test-signing-key-0123456789abcdef-0123456789";

    /// <summary>
    /// Rate limits, raised out of the way by default. TestServer connections carry no remote
    /// address, so every request in a test shares one bucket — a test that wants the limiter has
    /// to ask for it (see <c>RateLimitingTests</c>).
    /// </summary>
    public int AuthenticationPermitsPerMinute { get; init; } = 10_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Database", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:JwtSigningKey", SigningKey);
        builder.UseSetting(
            "RateLimiting:AuthenticationPermitsPerMinute",
            this.AuthenticationPermitsPerMinute.ToString(CultureInfo.InvariantCulture));
    }
}
