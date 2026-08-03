using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AngstromCommander.Protocol;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        // Connect to the hub as the Daemon and serve a canned listing and roots.
        await using var daemonConnection = BuildDaemonConnection(factory, token.AccessToken);
        daemonConnection.On<ListDirectoryRequest, ListDirectoryResponse>(
            DaemonHubMethods.ListDirectory,
            static request => ListDirectoryResponse.ForEntries(
            [
                new DirectoryEntry("hello.txt", IsDirectory: false, SizeBytes: 42, ModifiedAt: DateTimeOffset.UnixEpoch),
            ]));
        daemonConnection.On(
            DaemonHubMethods.ListRoots,
            static () => new ListRootsResponse([new SharedRoot("/data", Writable: false)]));
        await daemonConnection.StartAsync();

        // The user's machine list shows it online.
        var machines = await GetAsync<List<MachineResponse>>(userClient, new Uri("/api/daemons", UriKind.Relative));
        var machine = Assert.Single(machines);
        Assert.Equal("Test Machine", machine.DisplayName);
        Assert.True(machine.Online);

        // The machine tells its owner what it shares...
        var roots = await GetAsync<List<SharedRoot>>(
            userClient, new Uri($"/api/daemons/{claim.RegistrationId}/roots", UriKind.Relative));
        var root = Assert.Single(roots);
        Assert.Equal("/data", root.Path);
        Assert.False(root.Writable);

        // ...and the relay lists through it, end to end.
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
    public async Task ADaemonsConnectionTokenIsNotAUserToken()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "twotokens@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var pairing = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });
        var claim = await PostAsync<ClaimResponse>(
            userClient, "/api/enrollment/claim", new { code = pairing.Code, displayName = "Machine" });
        var challenge = await PostAsync<ChallengeResponse>(
            daemonClient, "/api/daemon-auth/challenge", new { registrationId = claim.RegistrationId });
        var signature = Convert.ToBase64String(
            key.SignData(Convert.FromBase64String(challenge.Nonce), HashAlgorithmName.SHA256));
        var daemonToken = await PostAsync<TokenResponse>(
            daemonClient, "/api/daemon-auth/token", new { registrationId = claim.RegistrationId, signature });

        // A connection token proves a machine owns its key. It says nothing about a user, so it
        // must not open the endpoints that act on a user's behalf.
        daemonClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", daemonToken.AccessToken);
        using var machines = await daemonClient.GetAsync(new Uri("/api/daemons", UriKind.Relative));
        using var listing = await daemonClient.GetAsync(
            new Uri($"/api/daemons/{claim.RegistrationId}/list?path=/data", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, machines.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listing.StatusCode);
    }

    [Fact]
    public async Task ARevokedMachineIsGoneForGood()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "revoked@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var registrationId = await EnrollDaemonAsync(factory, userClient);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.DaemonRegistrations
                .Where(r => r.Id == registrationId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow));
        }

        // Revoking has to cut both ways: the machine disappears from the account, and it can no
        // longer earn a connection token to dial back in with.
        var machines = await GetAsync<List<MachineResponse>>(userClient, new Uri("/api/daemons", UriKind.Relative));
        using var listing = await userClient.GetAsync(
            new Uri($"/api/daemons/{registrationId}/list?path=/data", UriKind.Relative));
        using var daemonClient = factory.CreateClient();
        using var challenge = await daemonClient.PostAsJsonAsync(
            "/api/daemon-auth/challenge", new { registrationId });

        Assert.Empty(machines);
        Assert.Equal(HttpStatusCode.NotFound, listing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);
    }

    [Fact]
    public async Task UnpairRevokesTheMachineAndTellsItSo()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "unpair@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // A connected Daemon listens for the courtesy push.
        var told = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On(DaemonHubMethods.Revoked, () => told.TrySetResult()));

        await using (connection)
        {
            using var unpair = await userClient.DeleteAsync(
                new Uri($"/api/daemons/{registrationId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, unpair.StatusCode);

            // The connected Daemon hears about it without waiting for a reconnect...
            await told.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // ...the machine is gone from the account, and every door is closed:
            // relay ops, the challenge, and a second unpair all answer as if it never was.
            var machines = await GetAsync<List<MachineResponse>>(userClient, new Uri("/api/daemons", UriKind.Relative));
            Assert.Empty(machines);

            using var listing = await userClient.GetAsync(
                new Uri($"/api/daemons/{registrationId}/list?path=/data", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, listing.StatusCode);

            using var daemonClient = factory.CreateClient();
            using var challenge = await daemonClient.PostAsJsonAsync(
                "/api/daemon-auth/challenge", new { registrationId });
            Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);

            using var again = await userClient.DeleteAsync(
                new Uri($"/api/daemons/{registrationId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }
    }

    [Fact]
    public async Task StrangersCannotUnpairSomeoneElsesMachine()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var ownerClient = factory.CreateClient();
        var ownerToken = await RegisterAndLoginAsync(ownerClient, "unpair-owner@example.com");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var registrationId = await EnrollDaemonAsync(factory, ownerClient);

        using var strangerClient = factory.CreateClient();
        var strangerToken = await RegisterAndLoginAsync(strangerClient, "unpair-stranger@example.com");
        strangerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", strangerToken);

        using var response = await strangerClient.DeleteAsync(
            new Uri($"/api/daemons/{registrationId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the owner's machine is untouched.
        var machines = await GetAsync<List<MachineResponse>>(ownerClient, new Uri("/api/daemons", UriKind.Relative));
        Assert.Single(machines);
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
    public async Task NewPairingCodeInvalidatesThePreviousUnclaimedOne()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "carol@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // The same machine asks twice (e.g. the Daemon restarted).
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var stale = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });
        var current = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });

        // The orphaned code pairs to nobody — claiming it must fail.
        using var staleClaim = await userClient.PostAsJsonAsync(
            "/api/enrollment/claim", new { code = stale.Code, displayName = "Ghost" });
        Assert.Equal(HttpStatusCode.NotFound, staleClaim.StatusCode);

        // The code the Daemon is actually polling works.
        var claim = await PostAsync<ClaimResponse>(
            userClient, "/api/enrollment/claim", new { code = current.Code, displayName = "Real" });
        Assert.Equal("Real", claim.DisplayName);
    }

    [Fact]
    public async Task OneCodeClaimedManyTimesAtOncePairsTheMachineExactlyOnce()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "racer@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var pairing = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });

        // Everyone reads "unclaimed" before anyone writes, so without a conditional update the
        // same machine ends up registered several times over.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt =>
            userClient.PostAsJsonAsync(
                "/api/enrollment/claim",
                new { code = pairing.Code, displayName = $"Racer {attempt}" })));

        try
        {
            Assert.Single(attempts, static attempt => attempt.IsSuccessStatusCode);
            var machines = await GetAsync<List<MachineResponse>>(userClient, new Uri("/api/daemons", UriKind.Relative));
            Assert.Single(machines);
        }
        finally
        {
            Array.ForEach(attempts, static attempt => attempt.Dispose());
        }
    }

    [Fact]
    public async Task TokenOfDeletedUserGetsUnauthorizedNotServerError()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);

        using var client = factory.CreateClient();
        var token = await RegisterAndLoginAsync(client, "ghost@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The account vanishes while the JWT is still valid (wiped dev DB, deleted user, …).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Email == "ghost@example.com").ExecuteDeleteAsync();
        }

        using var daemonKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(daemonKey.ExportSubjectPublicKeyInfo());
        var pairing = await PostAsync<PairingResponse>(
            client, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });

        using var response = await client.PostAsJsonAsync(
            "/api/enrollment/claim", new { code = pairing.Code, displayName = "Ghost Machine" });

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

    [Fact]
    public async Task DownloadStreamsFileBytesThroughRelay()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "dl@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // Big enough for several 64 KB chunks; random so corruption can't hide.
        var payload = new byte[300_000];
        RandomNumberGenerator.Fill(payload);

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<DownloadFileRequest, DownloadFileResponse>(
                DaemonHubMethods.DownloadFile,
                request =>
                {
                    _ = Task.Run(() => conn.InvokeAsync(
                        ServerHubMethods.UploadFileChunks, request.TransferId, ChunksOf(payload)));
                    return DownloadFileResponse.ForFile("blob.bin", payload.Length);
                }));

        await using (connection)
        {
            using var response = await userClient.GetAsync(
                new Uri($"/api/daemons/{registrationId}/download?path=/data/blob.bin", UriKind.Relative));

            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail(await response.Content.ReadAsStringAsync());
            }

            Assert.Contains("blob.bin", response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload, bytes);
        }
    }

    [Fact]
    public async Task FinishedDownloadsGiveTheirTransferSlotsBack()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "slots@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<DownloadFileRequest, DownloadFileResponse>(
                DaemonHubMethods.DownloadFile,
                request =>
                {
                    _ = Task.Run(() => conn.InvokeAsync(
                        ServerHubMethods.UploadFileChunks, request.TransferId, ChunksOf(payload)));
                    return DownloadFileResponse.ForFile("blob.bin", payload.Length);
                }));

        await using (connection)
        {
            // Comfortably more than one user may hold open at once: if a finished transfer kept
            // its slot, a busy session would start refusing downloads with none still running.
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                using var response = await userClient.GetAsync(
                    new Uri($"/api/daemons/{registrationId}/download?path=/data/blob.bin", UriKind.Relative));

                if (!response.IsSuccessStatusCode)
                {
                    Assert.Fail($"Download {attempt} failed: {await response.Content.ReadAsStringAsync()}");
                }

                Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
            }
        }
    }

    [Fact]
    public async Task DownloadSurfacesDaemonReportedError()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "dlerr@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, static conn =>
            conn.On<DownloadFileRequest, DownloadFileResponse>(
                DaemonHubMethods.DownloadFile,
                static request => DownloadFileResponse.ForError("Path is outside the allowed roots.")));

        await using (connection)
        {
            using var response = await userClient.GetAsync(
                new Uri($"/api/daemons/{registrationId}/download?path=/forbidden", UriKind.Relative));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task UploadStreamsRequestBodyToTheMachine()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "up@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var payload = new byte[300_000];
        RandomNumberGenerator.Fill(payload);
        var received = new MemoryStream();

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<UploadFileRequest, UploadFileResponse>(
                DaemonHubMethods.UploadFile,
                request =>
                {
                    // Stand in for the real Daemon: pull the bytes, then report completion.
                    _ = Task.Run(async () =>
                    {
                        await foreach (var chunk in conn.StreamAsync<byte[]>(
                            ServerHubMethods.DownloadFileChunks, request.TransferId))
                        {
                            received.Write(chunk);
                        }

                        await conn.InvokeAsync(ServerHubMethods.CompleteTransfer, request.TransferId, null);
                    });
                    return UploadFileResponse.ForSuccess();
                }));

        await using (connection)
        {
            using var content = new ByteArrayContent(payload);
            using var response = await userClient.PostAsync(
                new Uri($"/api/daemons/{registrationId}/upload?path=/uploads/blob.bin", UriKind.Relative), content);

            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail(await response.Content.ReadAsStringAsync());
            }

            var transferred = await response.Content.ReadFromJsonAsync<TransferredResponse>();
            Assert.Equal(payload.Length, transferred?.BytesTransferred);
            Assert.Equal(payload, received.ToArray());
        }
    }

    [Fact]
    public async Task UploadsStopWithTooManyRequestsWhenTheDailyQuotaIsSpent()
    {
        using var factory = new ServerFactory(postgres.ConnectionString) { DailyBytesPerUser = 100_000 };
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "quota@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<UploadFileRequest, UploadFileResponse>(
                DaemonHubMethods.UploadFile,
                request =>
                {
                    _ = Task.Run(async () =>
                    {
                        await foreach (var _ in conn.StreamAsync<byte[]>(
                            ServerHubMethods.DownloadFileChunks, request.TransferId))
                        {
                            // Drain and discard; the quota fails the transfer server-side.
                        }
                    });
                    return UploadFileResponse.ForSuccess();
                }));

        await using (connection)
        {
            // Three times the quota: the upload dies mid-body with the quota's own message.
            using var content = new ByteArrayContent(new byte[300_000]);
            using var response = await userClient.PostAsync(
                new Uri($"/api/daemons/{registrationId}/upload?path=/uploads/big.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("Daily transfer limit", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // And with the allowance spent, the next transfer is refused before any byte moves.
            using var secondContent = new ByteArrayContent([1]);
            using var second = await userClient.PostAsync(
                new Uri($"/api/daemons/{registrationId}/upload?path=/uploads/small.bin", UriKind.Relative),
                secondContent);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        }
    }

    [Fact]
    public async Task TransferUsageReportsWhatTheDayHasSpent()
    {
        using var factory = new ServerFactory(postgres.ConnectionString) { DailyBytesPerUser = 100_000 };
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "usage@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // Before any transfer, the allowance is untouched and the limit is the configured one.
        var before = await GetAsync<UsageResponse>(userClient, new Uri("/api/transfers/usage", UriKind.Relative));
        Assert.Equal(0, before.BytesUsedToday);
        Assert.Equal(100_000, before.DailyLimitBytes);

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<UploadFileRequest, UploadFileResponse>(
                DaemonHubMethods.UploadFile,
                request =>
                {
                    _ = Task.Run(async () =>
                    {
                        await foreach (var _ in conn.StreamAsync<byte[]>(
                            ServerHubMethods.DownloadFileChunks, request.TransferId))
                        {
                        }

                        await conn.InvokeAsync(ServerHubMethods.CompleteTransfer, request.TransferId, null);
                    });
                    return UploadFileResponse.ForSuccess();
                }));

        await using (connection)
        {
            using var content = new ByteArrayContent(new byte[50_000]);
            using var response = await userClient.PostAsync(
                new Uri($"/api/daemons/{registrationId}/upload?path=/uploads/metered.bin", UriKind.Relative), content);
            response.EnsureSuccessStatusCode();

            var after = await GetAsync<UsageResponse>(userClient, new Uri("/api/transfers/usage", UriKind.Relative));
            Assert.Equal(50_000, after.BytesUsedToday);
        }
    }

    [Fact]
    public async Task UploadSurfacesRefusalFromTheMachine()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "upfail@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var (registrationId, connection) = await ConnectDaemonAsync(factory, userClient, static conn =>
            conn.On<UploadFileRequest, UploadFileResponse>(
                DaemonHubMethods.UploadFile,
                static _ => UploadFileResponse.ForError("Path is not inside a writable root.")));

        await using (connection)
        {
            using var content = new ByteArrayContent([1, 2, 3]);
            using var response = await userClient.PostAsync(
                new Uri($"/api/daemons/{registrationId}/upload?path=/data/blocked.bin", UriKind.Relative), content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task CopyMovesBytesBetweenTwoMachines()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await RegisterAndLoginAsync(userClient, "copy@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var payload = new byte[200_000];
        RandomNumberGenerator.Fill(payload);
        var received = new MemoryStream();

        // The source machine serves the file...
        var (sourceId, sourceConnection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<DownloadFileRequest, DownloadFileResponse>(
                DaemonHubMethods.DownloadFile,
                request =>
                {
                    _ = Task.Run(() => conn.InvokeAsync(
                        ServerHubMethods.UploadFileChunks, request.TransferId, ChunksOf(payload)));
                    return DownloadFileResponse.ForFile("blob.bin", payload.Length);
                }));

        // ...and the target machine receives it.
        var (targetId, targetConnection) = await ConnectDaemonAsync(factory, userClient, conn =>
            conn.On<UploadFileRequest, UploadFileResponse>(
                DaemonHubMethods.UploadFile,
                request =>
                {
                    _ = Task.Run(async () =>
                    {
                        await foreach (var chunk in conn.StreamAsync<byte[]>(
                            ServerHubMethods.DownloadFileChunks, request.TransferId))
                        {
                            received.Write(chunk);
                        }

                        await conn.InvokeAsync(ServerHubMethods.CompleteTransfer, request.TransferId, null);
                    });
                    return UploadFileResponse.ForSuccess();
                }));

        await using (sourceConnection)
        await using (targetConnection)
        {
            using var response = await userClient.PostAsync(
                new Uri(
                    $"/api/daemons/{sourceId}/copy-to/{targetId}?sourcePath=/data/blob.bin&targetPath=/uploads/blob.bin",
                    UriKind.Relative),
                content: null);

            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail(await response.Content.ReadAsStringAsync());
            }

            Assert.Equal(payload, received.ToArray());
        }
    }

    private static async IAsyncEnumerable<byte[]> ChunksOf(byte[] payload)
    {
        const int chunkSize = 64 * 1024;
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            await Task.Yield();
            yield return payload[offset..Math.Min(offset + chunkSize, payload.Length)];
        }
    }

    private static async Task<(Guid RegistrationId, HubConnection Connection)> ConnectDaemonAsync(
        ServerFactory factory, HttpClient authenticatedUserClient, Action<HubConnection> configure)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var pairing = await PostAsync<PairingResponse>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });
        var claim = await PostAsync<ClaimResponse>(
            authenticatedUserClient, "/api/enrollment/claim", new { code = pairing.Code, displayName = "Machine" });
        var challenge = await PostAsync<ChallengeResponse>(
            daemonClient, "/api/daemon-auth/challenge", new { registrationId = claim.RegistrationId });
        var signature = Convert.ToBase64String(
            key.SignData(Convert.FromBase64String(challenge.Nonce), HashAlgorithmName.SHA256));
        var token = await PostAsync<TokenResponse>(
            daemonClient, "/api/daemon-auth/token", new { registrationId = claim.RegistrationId, signature });

        var connection = BuildDaemonConnection(factory, token.AccessToken);
        configure(connection);
        await connection.StartAsync();
        return (claim.RegistrationId, connection);
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

    private sealed record TransferredResponse(long BytesTransferred);

    private sealed record UsageResponse(long BytesUsedToday, long DailyLimitBytes);

    private sealed record MachineResponse(
        Guid RegistrationId, string DisplayName, string Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Online);
}
