using System.Net.Http.Headers;

namespace AngstromCommander.Server.Tests.Integration;

/// <summary>
/// The WebClient is served from its own origin, so every call it makes is cross-origin. These
/// cover the environments that are not Development, where the allowance is a named list.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CrossOriginTests(PostgresFixture postgres)
{
    private const string WebClientOrigin = "https://app.angstrom.example.com";

    [Fact]
    public async Task AConfiguredOriginIsAllowed()
    {
        using var factory = this.Factory([WebClientOrigin]);
        using var client = factory.CreateClient();

        using var response = await Preflight(client, WebClientOrigin);

        Assert.Equal(
            WebClientOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task AnUnknownOriginIsNot()
    {
        using var factory = this.Factory([WebClientOrigin]);
        using var client = factory.CreateClient();

        using var response = await Preflight(client, "https://not-ours.example.com");

        Assert.DoesNotContain("Access-Control-Allow-Origin", response.Headers.Select(static h => h.Key));
    }

    [Fact]
    public async Task AnEnvironmentWithNoConfiguredOriginAllowsNoBrowser()
    {
        // Failing shut matters more than convenience here: the alternative default would be to
        // accept any origin, which is exactly what must never reach a real environment.
        using var factory = this.Factory([]);
        using var client = factory.CreateClient();

        using var response = await Preflight(client, WebClientOrigin);

        Assert.DoesNotContain("Access-Control-Allow-Origin", response.Headers.Select(static h => h.Key));
    }

    [Fact]
    public async Task TheDownloadFileNameHeaderIsReadableCrossOrigin()
    {
        // Without this the browser hides Content-Disposition from the page and downloads land
        // under a name guessed from the URL instead of the one the machine reported.
        using var factory = this.Factory([WebClientOrigin]);
        using var client = factory.CreateClient();

        // Exposed headers are advertised on the real response, not on the preflight.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/healthz", UriKind.Relative));
        request.Headers.Add("Origin", WebClientOrigin);
        using var response = await client.SendAsync(request);

        Assert.Contains(
            "Content-Disposition",
            response.Headers.GetValues("Access-Control-Expose-Headers"),
            StringComparer.OrdinalIgnoreCase);
    }

    private ServerFactory Factory(string[] allowedOrigins)
    {
        return new ServerFactory(postgres.ConnectionString)
        {
            Environment = "Production",
            AllowedOrigins = allowedOrigins,
        };
    }

    private static async Task<HttpResponseMessage> Preflight(HttpClient client, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/daemons", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return await client.SendAsync(request);
    }
}
