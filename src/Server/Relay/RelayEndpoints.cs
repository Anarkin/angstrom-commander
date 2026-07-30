using AngstromCommander.Protocol;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
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
            static async Task<Ok<IReadOnlyList<MachineResponse>>> (
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                var registrations = await db.DaemonRegistrations.AsNoTracking()
                    .Where(r => r.UserId == userId && r.RevokedAt == null)
                    .OrderBy(static r => r.CreatedAt)
                    .ToListAsync(cancellationToken);

                IReadOnlyList<MachineResponse> machines = registrations
                    .Select(r => new MachineResponse(
                        r.Id,
                        r.DisplayName,
                        r.Platform,
                        r.CreatedAt,
                        r.LastSeenAt,
                        Online: registry.TryGetConnection(r.Id.ToString(), out _)))
                    .ToList();

                return TypedResults.Ok(machines);
            })
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/daemons/{registrationId:guid}/list",
            static async Task<Results<Ok<IReadOnlyList<DirectoryEntry>>, ProblemHttpResult>> (
                Guid registrationId,
                string path,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                // Authorize before routing: the registration must belong to the caller.
                if (!await OwnsMachineAsync(db, http, registrationId, cancellationToken))
                {
                    return NoSuchMachine();
                }

                if (!registry.TryGetConnection(registrationId.ToString(), out var connectionId))
                {
                    return MachineOffline();
                }

                ListDirectoryResponse response;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(OperationTimeout);
                    response = await hub.Clients.Client(connectionId)
                        .InvokeAsync<ListDirectoryResponse>(
                            DaemonHubMethods.ListDirectory,
                            new ListDirectoryRequest(path),
                            timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return MachineSilent();
                }

                if (response.Error is not null || response.Entries is null)
                {
                    return TypedResults.Problem(
                        detail: response.Error ?? "The machine sent no directory listing.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                return TypedResults.Ok(response.Entries);
            })
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/daemons/{registrationId:guid}/download",
            static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> (
                Guid registrationId,
                string path,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                FileTransferRegistry transfers,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                if (!await OwnsMachineAsync(db, http, registrationId, cancellationToken))
                {
                    return NoSuchMachine();
                }

                if (!registry.TryGetConnection(registrationId.ToString(), out var connectionId))
                {
                    return MachineOffline();
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
                    return MachineSilent();
                }

                if (response.Error is not null || response.FileName is null)
                {
                    transfers.Remove(transferId);
                    return TypedResults.Problem(
                        detail: response.Error ?? "The machine sent no file metadata.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                transfers.TryGetChannel(transferId, out var channel);
                return TypedResults.Stream(
                    destination => PumpTransferAsync(transfers, transferId, channel, destination, http.RequestAborted),
                    contentType: "application/octet-stream",
                    fileDownloadName: response.FileName);
            })
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }

    /// <summary>The problem responses every relayed file operation can answer with.</summary>
    private static RouteHandlerBuilder ProducesRelayProblems(this RouteHandlerBuilder builder)
    {
        return builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
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

    private static Task<bool> OwnsMachineAsync(
        AppDbContext db, HttpContext http, Guid registrationId, CancellationToken cancellationToken)
    {
        var userId = GetUserId(http);
        return db.DaemonRegistrations.AsNoTracking().AnyAsync(
            r => r.Id == registrationId && r.UserId == userId && r.RevokedAt == null,
            cancellationToken);
    }

    private static ProblemHttpResult NoSuchMachine()
    {
        return TypedResults.Problem(detail: "No such machine.", statusCode: StatusCodes.Status404NotFound);
    }

    private static ProblemHttpResult MachineOffline()
    {
        return TypedResults.Problem(
            detail: "The machine is not connected right now.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static ProblemHttpResult MachineSilent()
    {
        return TypedResults.Problem(
            detail: "The machine did not answer.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }

    private static Guid GetUserId(HttpContext http)
    {
        return Guid.Parse(http.User.FindFirst(AuthClaims.Subject)!.Value);
    }
}

/// <summary>One of the caller's paired machines.</summary>
internal sealed record MachineResponse(
    Guid RegistrationId,
    string DisplayName,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool Online);
