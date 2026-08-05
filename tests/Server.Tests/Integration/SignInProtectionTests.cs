using System.Net;
using System.Net.Http.Json;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

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

        // Asked of Identity rather than of the response body, because the response deliberately
        // does not say (see ALockedOutAccountIsIndistinguishableFromAnyOtherRefusal). Without
        // this the lockout could stop working and nothing outside would notice.
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True(await users.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task ALockedOutAccountIsIndistinguishableFromAnyOtherRefusal()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "enumerable@example.com";
        await RegisterAsync(client, email);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await LoginAsync(client, email, "Wr0ngPassword!");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // A lockout can only befall an account that exists, so an answer that mentions one is
        // an existence oracle: five wrong guesses would turn this endpoint into a membership
        // test for any address someone cared to try, which is the whole thing the identical
        // wrong-password/unknown-account answers exist to prevent.
        using var lockedOut = await LoginAsync(client, email, Password);
        using var unknown = await LoginAsync(client, "no-such-person@example.com", Password);

        Assert.Equal(unknown.StatusCode, lockedOut.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await lockedOut.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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
            "/api/auth/register",
            new { email = "newcomer@example.com", password = Password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(
            "Registration is closed",
            await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

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
            await wrongPassword.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await noSuchAccount.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task RegisteringATakenAddressDoesNotSaySoIsTaken()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "taken@example.com";
        await RegisterAsync(client, email);

        // Identity's own wording is "Email 'taken@example.com' is already taken", which hands
        // back the account-existence answer /api/auth/login is careful never to give.
        using var duplicate = await client.PostAsJsonAsync(
            "/api/auth/register", new { email, password = Password }, TestContext.Current.CancellationToken);

        Assert.False(duplicate.IsSuccessStatusCode);
        var body = await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("already taken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneCallerCannotSidestepTheLimitByChangingIPv6Address()
    {
        using var factory = new ServerFactory(postgres.ConnectionString) { AuthenticationPermitsPerMinute = 3 };
        using var client = factory.CreateClient();

        // Every one of these is a different address, and every one of them is the same
        // subscriber: an IPv6 customer is handed a /64 at least. Keyed on the full /128 the
        // limiter would hand each request a fresh bucket and never bind at all — which is
        // most of the internet walking past every anonymous limit on the Server.
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "v6@example.com", password = Password }),
            };
            request.Headers.Add("X-Forwarded-For", $"2001:db8:1234:5678::{attempt + 1}");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        // And a genuinely different subscriber — another /64 — is untouched by the first one.
        using var elsewhere = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "v6@example.com", password = Password }),
        };
        elsewhere.Headers.Add("X-Forwarded-For", "2001:db8:1234:9999::1");
        using var otherSubscriber = await client.SendAsync(elsewhere, TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherSubscriber.StatusCode);
    }

    [Fact]
    public async Task TheEnrollmentPollIsRateLimitedToo()
    {
        using var factory = new ServerFactory(postgres.ConnectionString) { EnrollmentPollPermitsPerMinute = 3 };
        using var client = factory.CreateClient();

        // Anonymous and cheap to call in a loop, so the documented "every anonymous endpoint is
        // rate limited per caller" has to cover it and not just sign-in.
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/enrollment/status/ABCD2345");
            request.Headers.Add("X-Forwarded-For", "198.51.100.7");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses[..^1]);
    }

    [Fact]
    public async Task TheDaemonChallengeIsRateLimitedToo()
    {
        using var factory = new ServerFactory(postgres.ConnectionString) { AuthenticationPermitsPerMinute = 3 };
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/daemon-auth/challenge")
            {
                Content = JsonContent.Create(new { registrationId = Guid.NewGuid() }),
            };
            request.Headers.Add("X-Forwarded-For", "198.51.100.9");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
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
