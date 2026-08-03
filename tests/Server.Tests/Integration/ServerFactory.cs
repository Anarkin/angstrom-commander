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

    /// <summary>
    /// Which environment to boot as, for the behaviour that differs outside Development. Left
    /// alone by default so the rest of the suite keeps whatever the test host picks.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>Browser origins allowed to call the API; only consulted outside Development.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>Per-user daily relay quota in bytes; null keeps the appsettings default.</summary>
    public long? DailyBytesPerUser { get; init; }

    /// <summary>
    /// Open by default so the suite can mint accounts freely whatever environment it boots;
    /// the test about the switch itself closes it.
    /// </summary>
    public bool RegistrationEnabled { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (this.Environment is not null)
        {
            builder.UseEnvironment(this.Environment);
        }

        builder.UseSetting("ConnectionStrings:Database", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:JwtSigningKey", SigningKey);
        builder.UseSetting(
            "RateLimiting:AuthenticationPermitsPerMinute",
            this.AuthenticationPermitsPerMinute.ToString(CultureInfo.InvariantCulture));

        if (this.DailyBytesPerUser is long dailyBytes)
        {
            builder.UseSetting(
                "TransferLimits:DailyBytesPerUser", dailyBytes.ToString(CultureInfo.InvariantCulture));
        }

        builder.UseSetting("Registration:Enabled", this.RegistrationEnabled ? "true" : "false");

        for (var index = 0; index < this.AllowedOrigins.Count; index++)
        {
            builder.UseSetting(
                $"Cors:AllowedOrigins:{index.ToString(CultureInfo.InvariantCulture)}", this.AllowedOrigins[index]);
        }
    }
}
