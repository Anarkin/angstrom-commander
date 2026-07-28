using System.Net;
using System.Net.Http.Json;
using AngstromCommander.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace AngstromCommander.Server.Tests.Relay;

public class RelayEndpointTests
{
    [Fact]
    public async Task ListRelaysThroughConnectedDaemon()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        await using var daemonConnection = BuildDaemonConnection(factory, "test-daemon");
        daemonConnection.On<ListDirectoryRequest, ListDirectoryResponse>(
            DaemonHubMethods.ListDirectory,
            static request => ListDirectoryResponse.ForEntries(
            [
                new DirectoryEntry("hello.txt", IsDirectory: false, SizeBytes: 42, ModifiedAt: DateTimeOffset.UnixEpoch),
            ]));
        await daemonConnection.StartAsync();

        using var response = await client.GetAsync(
            new Uri("/api/daemons/test-daemon/list?path=/data", UriKind.Relative));

        response.EnsureSuccessStatusCode();
        var entries = await response.Content.ReadFromJsonAsync<List<DirectoryEntry>>();
        Assert.NotNull(entries);
        var entry = Assert.Single(entries);
        Assert.Equal("hello.txt", entry.Name);
        Assert.Equal(42, entry.SizeBytes);
    }

    [Fact]
    public async Task ListReturnsNotFoundForUnknownDaemon()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/daemons/ghost/list?path=/data", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSurfacesDaemonReportedError()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        await using var daemonConnection = BuildDaemonConnection(factory, "err-daemon");
        daemonConnection.On<ListDirectoryRequest, ListDirectoryResponse>(
            DaemonHubMethods.ListDirectory,
            static request => ListDirectoryResponse.ForError("Path is outside the allowed roots."));
        await daemonConnection.StartAsync();

        using var response = await client.GetAsync(
            new Uri("/api/daemons/err-daemon/list?path=/forbidden", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HubConnection BuildDaemonConnection(WebApplicationFactory<Program> factory, string daemonId)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, $"/hub/daemon?daemonId={daemonId}"), options =>
            {
                // Route the SignalR traffic through the in-memory TestServer.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
    }
}
