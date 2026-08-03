using System.Net;
using System.Net.Http.Json;

namespace AngstromCommander.Server.Tests.Integration;

/// <summary>
/// What stops someone working through a password list: the account locks after a handful of
/// wrong guesses, and the endpoint itself only answers so many times a minute.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SignInProtectionTests(PostgresFixture postgres)
{
    private const string Password = "Sup3rSecret!";

    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAccountOut()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "locked@example.com";
        await RegisterAsync(client, email);

        // Identity's default allowance is five attempts.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await LoginAsync(client, email, "Wr0ngPassword!");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // The right password no longer helps — that is the point.
        using var lockedOut = await LoginAsync(client, email, Password);

        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);
        var problem = await lockedOut.Content.ReadAsStringAsync();
        Assert.Contains("Too many failed sign-in attempts", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClosedEnvironmentRefusesNewAccountsButServesExistingOnes()
    {
        using var openFactory = new ServerFactory(postgres.ConnectionString);
        using (var openClient = openFactory.CreateClient())
        {
            await RegisterAsync(openClient, "grandfathered@example.com");
        }

        // The same database, registration now closed — the standing-environment posture.
        using var closedFactory = new ServerFactory(postgres.ConnectionString) { RegistrationEnabled = false };
        using var client = closedFactory.CreateClient();

        using var refused = await client.PostAsJsonAsync(
            "/api/auth/register", new { email = "newcomer@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(
            "Registration is closed", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Closing the door to newcomers must not lock out the people already inside.
        using var login = await LoginAsync(client, "grandfathered@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task UnknownAccountAndWrongPasswordAnswerAlike()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "known@example.com";
        await RegisterAsync(client, email);

        using var wrongPassword = await LoginAsync(client, email, "Wr0ngPassword!");
        using var noSuchAccount = await LoginAsync(client, "nobody@example.com", Password);

        Assert.Equal(wrongPassword.StatusCode, noSuchAccount.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await noSuchAccount.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignInAttemptsAreRateLimited()
    {
        using var factory = new ServerFactory(postgres.ConnectionString) { AuthenticationPermitsPerMinute = 3 };
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await LoginAsync(client, "flood@example.com", Password);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses[..^1]);
    }

    private static async Task RegisterAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
    {
        return client.PostAsJsonAsync("/api/auth/login", new { email, password });
    }
}
