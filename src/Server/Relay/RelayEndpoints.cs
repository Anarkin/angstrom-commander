using AngstromCommander.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace AngstromCommander.Server.Relay;

internal static class RelayEndpoints
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/daemons/{daemonId}/list",
            async (
                string daemonId,
                string path,
                IDaemonConnectionRegistry registry,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                if (!registry.TryGetConnection(daemonId, out var connectionId))
                {
                    return Results.NotFound(new { error = $"Daemon '{daemonId}' is not connected." });
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(OperationTimeout);

                ListDirectoryResponse response;
                try
                {
                    response = await hub.Clients.Client(connectionId)
                        .InvokeAsync<ListDirectoryResponse>(
                            DaemonHubMethods.ListDirectory,
                            new ListDirectoryRequest(path),
                            timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return Results.Problem(
                        detail: $"Daemon '{daemonId}' did not answer.",
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }

                return response.Error is null
                    ? Results.Ok(response.Entries)
                    : Results.BadRequest(new { error = response.Error });
            });

        return app;
    }
}
