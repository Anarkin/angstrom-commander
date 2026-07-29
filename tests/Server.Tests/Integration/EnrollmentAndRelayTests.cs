using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AngstromCommander.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace AngstromCommander.Server.Tests.Integration;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class EnrollmentAndRelayTests(PostgresFixture postgres)
{
    private const string Password = "Sup3rSecret!";

    [Fact]
    public async Task FullEnrollmentAndRelayFlow()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        // A user registers and logs in.
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "owner@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // A fresh Daemon generates its keypair and requests a pairing code.
        using var daemonKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(daemonKey.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var pairing = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });

        // Unclaimed code polls as 204.
        using var pending = await daemonClient.GetAsync(EnrollmentStatusUri(pairing.Code));
        Assert.Equal(HttpStatusCode.NoContent, pending.StatusCode);

        // The logged-in user claims the code.
        var claim = await PostAsync<ClaimResponse>(
            userClient, "/api/enrollment/claim", new { code = pairing.Code, displayName = "Test Machine" });

        // The Daemon's poll now yields its registration id.
        var status = await GetAsync<StatusResponse>(daemonClient, EnrollmentStatusUri(pairing.Code));
        Assert.Equal(claim.RegistrationId, status.RegistrationId);

        // Challenge → sign with the private key → connection token.
        var challenge = await PostAsync<ChallengeResponse>(
            daemonClient, "/api/daemon-auth/challenge", new { registrationId = claim.RegistrationId });
        var signature = Convert.ToBase64String(
            daemonKey.SignData(Convert.FromBase64String(challenge.Nonce), HashAlgorithmName.SHA256));
        var token = await PostAsync<TokenResponse>(
            daemonClient, "/api/daemon-auth/token", new { registrationId = claim.RegistrationId, signature });

        // Connect to the hub as the Daemon and serve a canned listing.
        await using var daemonConnection = BuildDaemonConnection(factory, token.AccessToken);
        daemonConnection.On<ListDirectoryRequest, ListDirectoryResponse>(
            DaemonHubMethods.ListDirectory,
            static request => ListDirectoryResponse.ForEntries(
            [
                new DirectoryEntry("hello.txt", IsDirectory: false, SizeBytes: 42, ModifiedAt: DateTimeOffset.UnixEpoch),
            ]));
        await daemonConnection.StartAsync();

        // The user's machine list shows it online.
        var machines = await GetAsync<List<MachineResponse>>(userClient, new Uri("/api/daemons", UriKind.Relative));
        var machine = Assert.Single(machines);
        Assert.Equal("Test Machine", machine.DisplayName);
        Assert.True(machine.Online);

        // And the relay lists through it, end to end.
        var entries = await GetAsync<List<DirectoryEntry>>(
            userClient, new Uri($"/api/daemons/{claim.RegistrationId}/list?path=/data", UriKind.Relative));
        var entry = Assert.Single(entries);
        Assert.Equal("hello.txt", entry.Name);
    }

    [Fact]
    public async Task OtherUsersCannotSeeOrUseSomeoneElsesDaemon()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var ownerClient = factory.CreateClient();
        var ownerToken = await RegisterAndLoginAsync(ownerClient, "alice@example.com");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var registrationId = await EnrollDaemonAsync(factory, ownerClient);

        using var strangerClient = factory.CreateClient();
        var strangerToken = await RegisterAndLoginAsync(strangerClient, "mallory@example.com");
        strangerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", strangerToken);

        using var listResponse = await strangerClient.GetAsync(
            new Uri($"/api/daemons/{registrationId}/list?path=/data", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);

        var strangerMachines = await GetAsync<List<MachineResponse>>(
            strangerClient, new Uri("/api/daemons", UriKind.Relative));
        Assert.Empty(strangerMachines);
    }

    [Fact]
    public async Task RelayRequiresAuthentication()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var anonymousClient = factory.CreateClient();

        using var response = await anonymousClient.GetAsync(
            new Uri($"/api/daemons/{Guid.NewGuid()}/list?path=/data", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongKeyCannotGetConnectionToken()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var ownerClient = factory.CreateClient();
        var ownerToken = await RegisterAndLoginAsync(ownerClient, "bob@example.com");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var registrationId = await EnrollDaemonAsync(factory, ownerClient);

        using var attackerClient = factory.CreateClient();
        var challenge = await PostAsync<ChallengeResponse>(
            attackerClient, "/api/daemon-auth/challenge", new { registrationId });

        // Sign with a DIFFERENT key than the one registered.
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forgedSignature = Convert.ToBase64String(
            wrongKey.SignData(Convert.FromBase64String(challenge.Nonce), HashAlgorithmName.SHA256));

        using var response = await attackerClient.PostAsJsonAsync(
            "/api/daemon-auth/token", new { registrationId, signature = forgedSignature });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<Guid> EnrollDaemonAsync(ServerFactory factory, HttpClient authenticatedUserClient)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var pairing = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });
        var claim = await PostAsync<ClaimResponse>(
            authenticatedUserClient, "/api/enrollment/claim", new { code = pairing.Code, displayName = "Machine" });
        return claim.RegistrationId;
    }

    private static async Task<string> RegisterAndLoginAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        register.EnsureSuccessStatusCode();
        var login = await PostAsync<LoginResponse>(client, "/api/auth/login", new { email, password = Password });
        return login.AccessToken;
    }

    private static async Task<TResponse> PostAsync<TResponse>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private static async Task<TResponse> GetAsync<TResponse>(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private static Uri EnrollmentStatusUri(string code)
    {
        return new Uri($"/api/enrollment/status/{code}", UriKind.Relative);
    }

    private static HubConnection BuildDaemonConnection(ServerFactory factory, string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hub/daemon"), options =>
            {
                // Route the SignalR traffic through the in-memory TestServer.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();
    }

    private sealed record PairingResponse(string Code, DateTimeOffset ExpiresAt);

    private sealed record ClaimResponse(Guid RegistrationId, string DisplayName);

    private sealed record StatusResponse(Guid RegistrationId);

    private sealed record ChallengeResponse(string Nonce);

    private sealed record TokenResponse(string AccessToken);

    private sealed record LoginResponse(string AccessToken);

    private sealed record MachineResponse(
        Guid RegistrationId, string DisplayName, string Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Online);
}
