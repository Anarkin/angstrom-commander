using AngstromCommander.Protocol;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Relay;

internal static class RelayEndpoints
{
    private const int UploadChunkBytes = 64 * 1024;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);

    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's machine list, with live connection status.
        app.MapGet(
            "/api/daemons",
            static async Task<Ok<IReadOnlyList<MachineResponse>>> (
                HttpContext http,
                DaemonRelayOperations operations,
                CancellationToken cancellationToken) =>
                TypedResults.Ok(await operations.ListMachinesAsync(GetUserId(http), cancellationToken)))
            .RequireAuthorization(AuthPolicies.User);

        // How much of the daily relay allowance this user has spent — for the WebClient's
        // progress bar. Zero limit means the environment runs without one.
        app.MapGet(
            "/api/transfers/usage",
            static Ok<TransferUsageResponse> (
                HttpContext http,
                FileTransferRegistry transfers,
                Microsoft.Extensions.Options.IOptions<TransferLimitOptions> limits) =>
                TypedResults.Ok(new TransferUsageResponse(
                    transfers.BytesUsedToday(GetUserId(http)),
                    limits.Value.DailyBytesPerUser)))
            .RequireAuthorization(AuthPolicies.User);

        // The opposite of enrollment: a soft revoke. The row stays (auditable), but every
        // query filters on RevokedAt, so the machine vanishes from the list, relay ops
        // refuse it, and the challenge endpoint stops recognizing it — the Daemon reacts
        // to that refusal by discarding its registration and re-entering enrollment.
        app.MapDelete(
            "/api/daemons/{registrationId:guid}",
            static async Task<Results<NoContent, ProblemHttpResult>> (
                Guid registrationId,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                var registration = await db.DaemonRegistrations.FirstOrDefaultAsync(
                    r => r.Id == registrationId && r.UserId == userId && r.RevokedAt == null,
                    cancellationToken);
                if (registration is null)
                {
                    return NoSuchMachine();
                }

                registration.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                // Courtesy push so a connected Daemon drops the dead registration now
                // rather than on its next reconnect. Best effort: the revoke already
                // succeeded, and a Daemon that misses this learns the same thing from
                // the challenge refusal.
                if (registry.TryGetConnection(registrationId.ToString(), out var connectionId))
                {
                    try
                    {
                        await hub.Clients.Client(connectionId)
                            .SendAsync(DaemonHubMethods.Revoked, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                    {
                        // The socket died under us — which settles the matter anyway.
                    }
                }

                return TypedResults.NoContent();
            })
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthPolicies.User);

        // What the machine shares — the discoverability side of the Daemon's sandbox, so
        // a pane can offer real starting points instead of an empty path box.
        app.MapGet(
            "/api/daemons/{registrationId:guid}/roots",
            static async Task<Results<Ok<IReadOnlyList<SharedRoot>>, ProblemHttpResult>> (
                Guid registrationId,
                HttpContext http,
                DaemonRelayOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.ListRootsAsync(GetUserId(http), registrationId, cancellationToken);
                return MapFailure(result) is ProblemHttpResult problem
                    ? problem
                    : TypedResults.Ok(result.Value!);
            })
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/daemons/{registrationId:guid}/list",
            static async Task<Results<Ok<IReadOnlyList<DirectoryEntry>>, ProblemHttpResult>> (
                Guid registrationId,
                string path,
                HttpContext http,
                DaemonRelayOperations operations,
                CancellationToken cancellationToken) =>
            {
                var result = await operations.ListDirectoryAsync(
                    GetUserId(http), registrationId, path, cancellationToken);
                return MapFailure(result) is ProblemHttpResult problem
                    ? problem
                    : TypedResults.Ok(result.Value!);
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

                switch (transfers.TryCreate(
                    GetUserId(http), writerRegistrationId: registrationId, readerRegistrationId: null,
                    out var transferId))
                {
                    case CreateTransferResult.TooManyConcurrent:
                        return TooManyTransfers();
                    case CreateTransferResult.QuotaExhausted:
                        return QuotaExhausted();
                    default:
                        break;
                }

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

                if (!transfers.TryGetChannel(transferId, out var channel))
                {
                    // Only a disconnect can have taken it away between creating it and here.
                    return MachineSilent();
                }

                // Belt and braces on top of the stream callback's own cleanup: a response that
                // never reaches the callback — headers failing, the connection dying in between —
                // would otherwise leave the transfer behind, holding one of this user's slots
                // until their Daemon next reconnects. Removing twice is harmless.
                http.Response.OnCompleted(() =>
                {
                    transfers.Remove(transferId);
                    return Task.CompletedTask;
                });

                return TypedResults.Stream(
                    destination => PumpTransferAsync(transfers, transferId, channel, destination, http.RequestAborted),
                    contentType: "application/octet-stream",
                    fileDownloadName: response.FileName);
            })
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        app.MapPost(
            "/api/daemons/{registrationId:guid}/upload",
            static async Task<Results<Ok<TransferResponse>, ProblemHttpResult>> (
                Guid registrationId,
                string path,
                bool? overwrite,
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

                switch (transfers.TryCreate(
                    GetUserId(http), writerRegistrationId: null, readerRegistrationId: registrationId,
                    out var transferId))
                {
                    case CreateTransferResult.TooManyConcurrent:
                        return TooManyTransfers();
                    case CreateTransferResult.QuotaExhausted:
                        return QuotaExhausted();
                    default:
                        break;
                }

                try
                {
                    // The Daemon validates and answers before pulling anything, so a rejected
                    // path fails fast instead of after a long body upload.
                    UploadFileResponse accepted;
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(OperationTimeout);
                        accepted = await hub.Clients.Client(connectionId)
                            .InvokeAsync<UploadFileResponse>(
                                DaemonHubMethods.UploadFile,
                                new UploadFileRequest(transferId, path, overwrite ?? false),
                                timeout.Token);
                    }

                    if (accepted.Error is not null)
                    {
                        return TypedResults.Problem(
                            detail: accepted.Error, statusCode: StatusCodes.Status400BadRequest);
                    }

                    transfers.TryGetChannel(transferId, out var channel);
                    var bytesTransferred = await PumpRequestBodyAsync(
                        http.Request.Body, channel, transfers, transferId, cancellationToken);

                    // The receiver reports when the bytes are actually on disk.
                    var failure = await transfers.Completion(transferId).WaitAsync(ChunkTimeout, cancellationToken);
                    if (failure is not null)
                    {
                        return TypedResults.Problem(
                            detail: failure, statusCode: StatusCodes.Status400BadRequest);
                    }

                    return TypedResults.Ok(new TransferResponse(bytesTransferred));
                }
                catch (TransferQuotaExceededException ex)
                {
                    // Before the machine-fault catch below: the machine did nothing wrong,
                    // the caller's daily allowance ran out mid-upload.
                    return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status429TooManyRequests);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
                {
                    return MachineSilent();
                }
                finally
                {
                    transfers.Remove(transferId);
                }
            })
            .Accepts<Stream>("application/octet-stream")
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        // The three mutations share one shape: relay the request, surface the Daemon's
        // refusal as a 400 the user can read, answer 204 on success.
        app.MapPost(
            "/api/daemons/{registrationId:guid}/mkdir",
            static async Task<Results<NoContent, ProblemHttpResult>> (
                Guid registrationId, string path, HttpContext http,
                DaemonRelayOperations operations, CancellationToken ct) =>
                MapMutation(await operations.CreateDirectoryAsync(GetUserId(http), registrationId, path, ct)))
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        // Rename is a move with the same parent; the WebClient's Rename button calls this.
        app.MapPost(
            "/api/daemons/{registrationId:guid}/move",
            static async Task<Results<NoContent, ProblemHttpResult>> (
                Guid registrationId, string sourcePath, string targetPath, bool? overwrite, HttpContext http,
                DaemonRelayOperations operations, CancellationToken ct) =>
                MapMutation(await operations.MoveEntryAsync(
                    GetUserId(http), registrationId, sourcePath, targetPath, overwrite ?? false, ct)))
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        app.MapDelete(
            "/api/daemons/{registrationId:guid}/entries",
            static async Task<Results<NoContent, ProblemHttpResult>> (
                Guid registrationId, string path, HttpContext http,
                DaemonRelayOperations operations, CancellationToken ct) =>
                MapMutation(await operations.DeleteEntryAsync(GetUserId(http), registrationId, path, ct)))
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        // The dual-pane move: bytes go source Daemon -> Server -> target Daemon without ever
        // being buffered whole, and without a round trip through the client.
        app.MapPost(
            "/api/daemons/{sourceRegistrationId:guid}/copy-to/{targetRegistrationId:guid}",
            static async Task<Results<Ok<TransferResponse>, ProblemHttpResult>> (
                Guid sourceRegistrationId,
                Guid targetRegistrationId,
                string sourcePath,
                string targetPath,
                bool? overwrite,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                FileTransferRegistry transfers,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                if (!await OwnsMachineAsync(db, http, sourceRegistrationId, cancellationToken)
                    || !await OwnsMachineAsync(db, http, targetRegistrationId, cancellationToken))
                {
                    return NoSuchMachine();
                }

                if (!registry.TryGetConnection(sourceRegistrationId.ToString(), out var sourceConnectionId)
                    || !registry.TryGetConnection(targetRegistrationId.ToString(), out var targetConnectionId))
                {
                    return MachineOffline();
                }

                switch (transfers.TryCreate(
                    GetUserId(http),
                    writerRegistrationId: sourceRegistrationId,
                    readerRegistrationId: targetRegistrationId,
                    out var transferId))
                {
                    case CreateTransferResult.TooManyConcurrent:
                        return TooManyTransfers();
                    case CreateTransferResult.QuotaExhausted:
                        return QuotaExhausted();
                    default:
                        break;
                }

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(OperationTimeout);

                    var opened = await hub.Clients.Client(sourceConnectionId)
                        .InvokeAsync<DownloadFileResponse>(
                            DaemonHubMethods.DownloadFile,
                            new DownloadFileRequest(transferId, sourcePath),
                            timeout.Token);
                    if (opened.Error is not null || opened.FileName is null)
                    {
                        return TypedResults.Problem(
                            detail: opened.Error ?? "The source machine sent no file metadata.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    var accepted = await hub.Clients.Client(targetConnectionId)
                        .InvokeAsync<UploadFileResponse>(
                            DaemonHubMethods.UploadFile,
                            new UploadFileRequest(transferId, targetPath, overwrite ?? false),
                            timeout.Token);
                    if (accepted.Error is not null)
                    {
                        return TypedResults.Problem(
                            detail: accepted.Error, statusCode: StatusCodes.Status400BadRequest);
                    }

                    var failure = await transfers.Completion(transferId).WaitAsync(ChunkTimeout, cancellationToken);
                    if (failure is not null)
                    {
                        return TypedResults.Problem(
                            detail: failure, statusCode: StatusCodes.Status400BadRequest);
                    }

                    return TypedResults.Ok(new TransferResponse(opened.SizeBytes ?? 0));
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
                {
                    return MachineSilent();
                }
                finally
                {
                    transfers.Remove(transferId);
                }
            })
            .ProducesRelayProblems()
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }

    /// <summary>The REST spelling of a relay failure; null when the operation succeeded.</summary>
    private static ProblemHttpResult? MapFailure<T>(RelayResult<T> result)
    {
        return result.Failure switch
        {
            RelayFailure.None => null,
            RelayFailure.NoSuchMachine => NoSuchMachine(),
            RelayFailure.MachineOffline => MachineOffline(),
            RelayFailure.MachineSilent => MachineSilent(),
            _ => TypedResults.Problem(
                detail: result.RefusalDetail, statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private static Results<NoContent, ProblemHttpResult> MapMutation(RelayResult<bool> result)
    {
        return MapFailure(result) is ProblemHttpResult problem ? problem : TypedResults.NoContent();
    }

    private static async Task<long> PumpRequestBodyAsync(
        Stream body,
        System.Threading.Channels.Channel<byte[]> channel,
        FileTransferRegistry transfers,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        var total = 0L;
        try
        {
            while (true)
            {
                var buffer = new byte[UploadChunkBytes];
                var read = await body.ReadAtLeastAsync(
                    buffer, UploadChunkBytes, throwOnEndOfStream: false, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;

                // Quota + bandwidth for the upload direction; the hub's incoming stream
                // is the same gate for the other two transfer shapes.
                await transfers.MeterAsync(transferId, read, cancellationToken);

                // Rolling per-chunk timeout, the mirror of the one on the download side: the
                // channel is bounded, so a receiving Daemon that stops draining it blocks this
                // write, and without a deadline the request would hang for as long as the client
                // kept the body open.
                using var chunkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                chunkTimeout.CancelAfter(ChunkTimeout);
                await channel.Writer.WriteAsync(
                    read == UploadChunkBytes ? buffer : buffer[..read], chunkTimeout.Token);
            }

            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            throw;
        }

        return total;
    }

    /// <summary>The problem responses every relayed file operation can answer with.</summary>
    private static RouteHandlerBuilder ProducesRelayProblems(this RouteHandlerBuilder builder)
    {
        return builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
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

    private static ProblemHttpResult TooManyTransfers()
    {
        return TypedResults.Problem(
            detail: "Too many transfers are already in progress. Wait for one to finish.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static ProblemHttpResult QuotaExhausted()
    {
        return TypedResults.Problem(
            detail: TransferQuotaExceededException.UserFacingMessage,
            statusCode: StatusCodes.Status429TooManyRequests);
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

/// <summary>How many bytes a completed transfer moved.</summary>
internal sealed record TransferResponse(long BytesTransferred);

/// <summary>How much of the caller's daily relay allowance is spent; a zero limit means none is configured.</summary>
internal sealed record TransferUsageResponse(long BytesUsedToday, long DailyLimitBytes);

/// <summary>One of the caller's paired machines.</summary>
internal sealed record MachineResponse(
    Guid RegistrationId,
    string DisplayName,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool Online);
