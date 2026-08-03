using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AngstromCommander.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace AngstromCommander.Server.Tests.Integration;

/// <summary>
/// The MCP endpoint spoken raw: JSON-RPC over Streamable HTTP, authenticated with a
/// personal access token — exactly what a third-party MCP client does.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class McpEndpointTests(PostgresFixture postgres)
{
    private const string Password = "Sup3rSecret!";

    [Fact]
    public async Task PatLifecycleMintsListsAndRevokes()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, "patlife@example.com");

        // Create: the only response that ever carries the token value.
        var created = await PostAsync<PatCreated>(client, "/api/pats", new { name = "Claude Code" });
        Assert.StartsWith("acpat_", created.Token, StringComparison.Ordinal);

        // List: named, dated, value withheld.
        var listed = await GetAsync<List<PatListed>>(client, "/api/pats");
        var pat = Assert.Single(listed);
        Assert.Equal("Claude Code", pat.Name);

        // Revoke: gone from the list, and the door closes (proved in the MCP tests below).
        using var revoked = await client.DeleteAsync(new Uri($"/api/pats/{created.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Empty(await GetAsync<List<PatListed>>(client, "/api/pats"));
    }

    [Fact]
    public async Task McpToolsOperateAMachineWithAPat()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        await AuthenticateAsync(userClient, "mcp@example.com");

        // A canned Daemon answers roots and mkdir, like a real one would.
        var (_, connection) = await ConnectCannedDaemonAsync(factory, userClient);

        await using (connection)
        {
            var created = await PostAsync<PatCreated>(userClient, "/api/pats", new { name = "mcp test" });

            using var mcpClient = factory.CreateClient();
            mcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);

            var initialize = await CallMcpAsync(mcpClient, """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
                """);
            Assert.Contains("protocolVersion", initialize, StringComparison.Ordinal);

            var tools = await CallMcpAsync(mcpClient, """
                {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
                """);
            Assert.Contains("list_machines", tools, StringComparison.Ordinal);
            Assert.Contains("delete_entry", tools, StringComparison.Ordinal);

            var machines = await CallMcpAsync(mcpClient, """
                {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_machines","arguments":{}}}
                """);
            Assert.Contains("Machine", machines, StringComparison.Ordinal);

            // A tool that reaches through the relay to the (canned) Daemon and back.
            var registrationId = ExtractRegistrationId(machines);
            const string mkdirTemplate = """
                {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"create_directory","arguments":{"registrationId":"REGISTRATION_ID","path":"/uploads/from-mcp"}}}
                """;
            var mkdir = await CallMcpAsync(
                mcpClient, mkdirTemplate.Replace("REGISTRATION_ID", registrationId.ToString(), StringComparison.Ordinal));
            Assert.Contains("Created /uploads/from-mcp", mkdir, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheMcpDoorOnlyOpensForALivePat()
    {
        using var factory = new ServerFactory(postgres.ConnectionString);
        using var userClient = factory.CreateClient();
        var userToken = await AuthenticateAsync(userClient, "mcpauth@example.com");
        var created = await PostAsync<PatCreated>(userClient, "/api/pats", new { name = "doomed" });

        // No credential at all.
        using var anonymous = factory.CreateClient();
        using var refused = await SendMcpAsync(anonymous, InitializeBody);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // A user JWT is not an MCP credential: the endpoint's audience is PATs alone.
        using var jwtClient = factory.CreateClient();
        jwtClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var wrongKind = await SendMcpAsync(jwtClient, InitializeBody);
        Assert.True(
            wrongKind.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403, got {wrongKind.StatusCode}.");

        // A PAT is not a REST credential either — the separation cuts both ways.
        using var patOnRest = factory.CreateClient();
        patOnRest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        using var restRefused = await patOnRest.GetAsync(new Uri("/api/daemons", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, restRefused.StatusCode);

        // And revocation slams the door mid-flight.
        using var revoke = await userClient.DeleteAsync(new Uri($"/api/pats/{created.Id}", UriKind.Relative));
        revoke.EnsureSuccessStatusCode();
        using var afterRevoke = factory.CreateClient();
        afterRevoke.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        using var dead = await SendMcpAsync(afterRevoke, InitializeBody);
        Assert.Equal(HttpStatusCode.Unauthorized, dead.StatusCode);
    }

    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
        """;

    private static async Task<HttpResponseMessage> SendMcpAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(request);
    }

    /// <summary>Sends one JSON-RPC message and returns the response payload as text (SSE unwrapped).</summary>
    private static async Task<string> CallMcpAsync(HttpClient client, string body)
    {
        using var response = await SendMcpAsync(client, body);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"MCP call failed ({response.StatusCode}): {content}");
        }

        // Streamable HTTP answers either plain JSON or a one-message SSE stream.
        if (!content.StartsWith("event:", StringComparison.Ordinal) &&
            !content.StartsWith("data:", StringComparison.Ordinal))
        {
            return content;
        }

        var data = content.Split('\n')
            .Where(static line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(static line => line["data:".Length..].Trim());
        return string.Join("\n", data);
    }

    private static Guid ExtractRegistrationId(string machinesPayload)
    {
        // The tool result nests JSON; rather than modeling the envelope, fish out the
        // first GUID-shaped value after a registrationId key.
        var index = machinesPayload.IndexOf("registrationId", StringComparison.OrdinalIgnoreCase);
        Assert.True(index >= 0, $"No registrationId in: {machinesPayload}");
        var tail = machinesPayload[index..];
        var match = System.Text.RegularExpressions.Regex.Match(
            tail, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        Assert.True(match.Success, $"No GUID in: {tail[..Math.Min(200, tail.Length)]}");
        return Guid.Parse(match.Value);
    }

    private static async Task<(Guid RegistrationId, HubConnection Connection)> ConnectCannedDaemonAsync(
        ServerFactory factory, HttpClient authenticatedUserClient)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var daemonClient = factory.CreateClient();
        var pairing = await PostAsync<Pairing>(
            daemonClient, "/api/enrollment/code", new { publicKeySpki, platform = "TestOS" });
        var claim = await PostAsync<Claimed>(
            authenticatedUserClient, "/api/enrollment/claim", new { code = pairing.Code, displayName = "Machine" });
        var challenge = await PostAsync<Challenge>(
            daemonClient, "/api/daemon-auth/challenge", new { registrationId = claim.RegistrationId });
        var signature = Convert.ToBase64String(key.SignData(
            Convert.FromBase64String(challenge.Nonce), System.Security.Cryptography.HashAlgorithmName.SHA256));
        var token = await PostAsync<Token>(
            daemonClient, "/api/daemon-auth/token", new { registrationId = claim.RegistrationId, signature });

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hub/daemon"), options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token.AccessToken);
            })
            .Build();
        connection.On<CreateDirectoryRequest, FileOperationResponse>(
            DaemonHubMethods.CreateDirectory,
            static request => request.Path.StartsWith("/uploads", StringComparison.Ordinal)
                ? FileOperationResponse.Success
                : FileOperationResponse.ForError("Path is not inside a writable root."));
        await connection.StartAsync();
        return (claim.RegistrationId, connection);
    }

    private static async Task<string> AuthenticateAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        register.EnsureSuccessStatusCode();
        var login = await PostAsync<Login>(client, "/api/auth/login", new { email, password = Password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return login.AccessToken;
    }

    private static async Task<TResponse> PostAsync<TResponse>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private static async Task<TResponse> GetAsync<TResponse>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private sealed record PatCreated(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);

    private sealed record PatListed(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    private sealed record Pairing(string Code, DateTimeOffset ExpiresAt);

    private sealed record Claimed(Guid RegistrationId, string DisplayName);

    private sealed record Challenge(string Nonce);

    private sealed record Token(string AccessToken);

    private sealed record Login(string AccessToken);
}
