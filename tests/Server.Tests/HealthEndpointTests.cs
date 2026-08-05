using Microsoft.AspNetCore.Mvc.Testing;

namespace AngstromCommander.Server.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task HealthzReturnsOk()
    {
        // No database and no signing key in this test: health must not depend on either.
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(
            static builder => builder.UseSetting("Database:MigrateOnStartup", "false"));
        using var client = factory.CreateClient();

        var response = await client.GetStringAsync(
            new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("ok", response);
    }
}
