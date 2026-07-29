using AngstromCommander.Protocol;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Relay;

internal static class RelayEndpoints
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);

    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's machine list, with live connection status.
        app.MapGet(
            "/api/daemons",
            static async (HttpContext http, AppDbContext db, IDaemonConnectionRegistry registry, CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                var registrations = await db.DaemonRegistrations.AsNoTracking()
                    .Where(r => r.UserId == userId && r.RevokedAt == null)
                    .OrderBy(static r => r.CreatedAt)
                    .ToListAsync(cancellationToken);

                return Results.Ok(registrations.Select(r => new
                {
                    registrationId = r.Id,
                    displayName = r.DisplayName,
                    platform = r.Platform,
                    createdAt = r.CreatedAt,
                    lastSeenAt = r.LastSeenAt,
                    online = registry.TryGetConnection(r.Id.ToString(), out _),
                }));
            })
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/daemons/{registrationId:guid}/list",
            static async (
                Guid registrationId,
                string path,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                // Authorize before routing: the registration must belong to the caller.
                var userId = GetUserId(http);
                var owned = await db.DaemonRegistrations.AsNoTracking().AnyAsync(
                    r => r.Id == registrationId && r.UserId == userId && r.RevokedAt == null,
                    cancellationToken);
                if (!owned)
                {
                    return Results.NotFound(new { error = "No such machine." });
                }

                if (!registry.TryGetConnection(registrationId.ToString(), out var connectionId))
                {
                    return Results.Problem(
                        detail: "The machine is not connected right now.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
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
                        detail: "The machine did not answer.",
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }

                return response.Error is null
                    ? Results.Ok(response.Entries)
                    : Results.BadRequest(new { error = response.Error });
            })
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/daemons/{registrationId:guid}/download",
            static async (
                Guid registrationId,
                string path,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                FileTransferRegistry transfers,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                var owned = await db.DaemonRegistrations.AsNoTracking().AnyAsync(
                    r => r.Id == registrationId && r.UserId == userId && r.RevokedAt == null,
                    cancellationToken);
                if (!owned)
                {
                    return Results.NotFound(new { error = "No such machine." });
                }

                if (!registry.TryGetConnection(registrationId.ToString(), out var connectionId))
                {
                    return Results.Problem(
                        detail: "The machine is not connected right now.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var transferId = transfers.Create(registrationId);
                DownloadFileResponse response;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(OperationTimeout);
                    response = await hub.Clients.Client(connectionId)
                        .InvokeAsync<DownloadFileResponse>(
                            DaemonHubMethods.DownloadFile,
                            new DownloadFileRequest(transferId, path),
                            timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    transfers.Remove(transferId);
                    return Results.Problem(
                        detail: "The machine did not answer.",
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }

                if (response.Error is not null || response.FileName is null)
                {
                    transfers.Remove(transferId);
                    return Results.BadRequest(new { error = response.Error ?? "The machine sent no file metadata." });
                }

                transfers.TryGetChannel(transferId, out var channel);
                return Results.Stream(
                    destination => PumpTransferAsync(transfers, transferId, channel, destination, http.RequestAborted),
                    contentType: "application/octet-stream",
                    fileDownloadName: response.FileName);
            })
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }

    private static async Task PumpTransferAsync(
        FileTransferRegistry transfers,
        Guid transferId,
        System.Threading.Channels.Channel<byte[]> channel,
        Stream destination,
        CancellationToken requestAborted)
    {
        try
        {
            while (true)
            {
                // Rolling per-chunk timeout: a Daemon that stops sending mid-transfer
                // must not hold the response open forever.
                using var chunkTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
                chunkTimeout.CancelAfter(ChunkTimeout);
                if (!await channel.Reader.WaitToReadAsync(chunkTimeout.Token))
                {
                    break;
                }

                while (channel.Reader.TryRead(out var chunk))
                {
                    await destination.WriteAsync(chunk, requestAborted);
                }
            }
        }
        finally
        {
            // Also unblocks the Daemon-side hub invocation if the client went away.
            transfers.Remove(transferId);
        }
    }

    private static Guid GetUserId(HttpContext http)
    {
        return Guid.Parse(http.User.FindFirst(AuthClaims.Subject)!.Value);
    }
}
