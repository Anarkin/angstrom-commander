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

                var transferId = transfers.Create(writerRegistrationId: registrationId, readerRegistrationId: null);
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

                var transferId = transfers.Create(writerRegistrationId: null, readerRegistrationId: registrationId);
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
                    var bytesTransferred = await PumpRequestBodyAsync(http.Request.Body, channel, cancellationToken);

                    // The receiver reports when the bytes are actually on disk.
                    var failure = await transfers.Completion(transferId).WaitAsync(ChunkTimeout, cancellationToken);
                    if (failure is not null)
                    {
                        return TypedResults.Problem(
                            detail: failure, statusCode: StatusCodes.Status400BadRequest);
                    }

                    return TypedResults.Ok(new TransferResponse(bytesTransferred));
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

                var transferId = transfers.Create(
                    writerRegistrationId: sourceRegistrationId, readerRegistrationId: targetRegistrationId);
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

    private static async Task<long> PumpRequestBodyAsync(
        Stream body,
        System.Threading.Channels.Channel<byte[]> channel,
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

/// <summary>How many bytes a completed transfer moved.</summary>
internal sealed record TransferResponse(long BytesTransferred);

/// <summary>One of the caller's paired machines.</summary>
internal sealed record MachineResponse(
    Guid RegistrationId,
    string DisplayName,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool Online);
