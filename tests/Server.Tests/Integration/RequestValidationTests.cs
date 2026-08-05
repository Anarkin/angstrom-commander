using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AngstromCommander.Server.Tests.Integration;

/// <summary>
/// Oversized and missing fields are the caller's mistake, not the Server's: they belong in a 400
/// from the endpoint, not a 500 out of the database driver when the value meets its column.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RequestValidationTests(PostgresFixture postgres)
{
    private const string Password = "Sup3rSecret!";

    [Fact]
    public async Task AnOversizedMachineNameIsRefusedNotStored()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var token = await RegisterAndLoginAsync(client, "namer@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsJsonAsync(
            "/api/enrollment/claim",
            new { code = "ABCD2345", displayName = new string('x', 201) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyMachineNameIsRefused()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var token = await RegisterAndLoginAsync(client, "blank@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsJsonAsync(
            "/api/enrollment/claim",
            new { code = "ABCD2345", displayName = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnOversizedPublicKeyIsRefused()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/enrollment/code",
            new { publicKeySpki = new string('A', 1001), platform = "TestOS" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnAbsurdlyLongPasswordIsRefusedBeforeItIsHashed()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = "long@example.com", password = new string('p', 4096) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SomethingThatIsNotAnEmailAddressIsRefused()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = "not-an-email", password = Password },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> RegisterAndLoginAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        register.EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginBody>();
        return body!.AccessToken;
    }

    private sealed record LoginBody(string AccessToken);
}
