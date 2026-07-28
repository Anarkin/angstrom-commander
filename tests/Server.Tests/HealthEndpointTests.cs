using Microsoft.AspNetCore.Mvc.Testing;

namespace AngstromCommander.Server.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task HealthzReturnsOk()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetStringAsync(new Uri("/healthz", UriKind.Relative));

        Assert.Equal("ok", response);
    }
}
