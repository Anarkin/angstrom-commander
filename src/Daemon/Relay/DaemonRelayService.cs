using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Daemon.Identity;
using AngstromCommander.Daemon.ServerApi;
using AngstromCommander.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Daemon.Relay;

/// <summary>
/// Enrolls this machine if needed (pairing-code flow), then holds the Daemon's single
/// persistent outbound connection to the Server open, authenticating with a token earned
/// by signing the Server's challenge, and answers relay requests pushed down the socket.
/// </summary>
internal sealed partial class DaemonRelayService(
    IOptions<RelayOptions> options,
    DaemonIdentityStore identityStore,
    ServerApiClient serverApi,
    ListDirectoryHandler listDirectoryHandler,
    DownloadFileHandler downloadFileHandler,
    UploadFileHandler uploadFileHandler,
    ILogger<DaemonRelayService> logger) : BackgroundService
{
    private const int DownloadChunkBytes = 64 * 1024;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EnrollmentPollDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var relayOptions = options.Value;
        if (relayOptions.ServerUrl is null)
        {
            LogNotConfigured(logger);
            return;
        }

        // One iteration per machine identity: when the Server disowns the current
        // registration (the user unpaired this machine), the dead registration is
        // discarded and the next iteration re-enrolls from scratch, TV-style — back
        // to showing a pairing code. Transient failures never land here; they are
        // retried inside the iteration forever.
        while (!stoppingToken.IsCancellationRequested)
        {
            var registrationId = identityStore.LoadRegistrationId()
                ?? await this.EnrollAsync(stoppingToken);

            // Completed only on the Server's explicit verdict: a Revoked push while
            // connected, or the challenge refusing to recognize the registration.
            var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var again = await this.RunConnectionAsync(relayOptions, registrationId, revoked, stoppingToken);
            if (!again)
            {
                return;
            }

            LogRevoked(logger);
            identityStore.ClearRegistrationId();
        }
    }

    /// <summary>Holds one connection for one registration; true = revoked, go re-enroll.</summary>
    private async Task<bool> RunConnectionAsync(
        RelayOptions relayOptions,
        Guid registrationId,
        TaskCompletionSource revoked,
        CancellationToken stoppingToken)
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(relayOptions.ServerUrl!, "/hub/daemon"), hubOptions =>
                hubOptions.AccessTokenProvider = () => this.GetConnectionTokenAsync(registrationId, revoked))
            .WithAutomaticReconnect(new AlwaysRetryPolicy())
            .Build();

        connection.On(DaemonHubMethods.Revoked, () => revoked.TrySetResult());

        connection.On<ListDirectoryRequest, ListDirectoryResponse>(
            DaemonHubMethods.ListDirectory,
            request => listDirectoryHandler.Handle(request));

        connection.On<DownloadFileRequest, DownloadFileResponse>(
            DaemonHubMethods.DownloadFile,
            request =>
            {
                var response = downloadFileHandler.Open(request, out var file);
                if (file is not null)
                {
                    // The metadata response returns first; the bytes follow as a separate
                    // streaming invocation tagged with the transfer id, which takes ownership
                    // of the open file from here.
#pragma warning disable CA2025 // Handing the stream over IS the contract: PumpFileAsync closes it.
                    _ = PumpFileAsync(connection, request.TransferId, file, logger, stoppingToken);
#pragma warning restore CA2025
                }

                return response;
            });

        connection.On<UploadFileRequest, UploadFileResponse>(
            DaemonHubMethods.UploadFile,
            request =>
            {
                var response = uploadFileHandler.Accept(request, out var resolvedPath);
                if (response.Error is null)
                {
                    // Accepted: pull the bytes ourselves (the Server cannot push a stream to us)
                    // and report completion when they are on disk.
                    _ = this.PullFileAsync(connection, request, resolvedPath, stoppingToken);
                }

                return response;
            });

        while (!stoppingToken.IsCancellationRequested && !revoked.Task.IsCompleted)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                LogConnected(logger, relayOptions.ServerUrl!, registrationId);
                break;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TimeoutException)
            {
                LogConnectFailed(logger, relayOptions.ServerUrl!, RetryDelay.TotalSeconds, ex);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        // Stay alive until shutdown or revocation; WithAutomaticReconnect keeps the
        // socket healthy in between (and a reconnect that meets the challenge refusal
        // completes the revoked signal through the token provider).
        await Task.WhenAny(revoked.Task, Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken));
        return revoked.Task.IsCompleted && !stoppingToken.IsCancellationRequested;
    }

    private async Task<Guid> EnrollAsync(CancellationToken cancellationToken)
    {
        string publicKeySpki;
        using (var key = identityStore.GetOrCreateKey())
        {
            publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pairing = await serverApi.RequestPairingCodeAsync(
                    publicKeySpki, RuntimeInformation.OSDescription, cancellationToken);
                LogPairingCode(logger, pairing.Code);

                while (DateTimeOffset.UtcNow < pairing.ExpiresAt)
                {
                    await Task.Delay(EnrollmentPollDelay, cancellationToken);
                    if (await serverApi.GetEnrollmentStatusAsync(pairing.Code, cancellationToken) is Guid registrationId)
                    {
                        identityStore.SaveRegistrationId(registrationId);
                        LogEnrolled(logger, registrationId);
                        return registrationId;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                LogEnrollmentRetry(logger, RetryDelay.TotalSeconds, ex);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Streams an opened file to the Server and closes it afterwards, whether or not the transfer
    /// ever got going. The chunk iterator is lazy, so a failure before the first read — a dropped
    /// socket, a transfer the Server no longer knows — would leave the handle open for the life of
    /// the process if the stream's fate were left to the iterator.
    /// </summary>
    internal static async Task PumpFileAsync(
        HubConnection connection,
        Guid transferId,
        FileStream file,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.InvokeAsync(
                ServerHubMethods.UploadFileChunks,
                transferId,
                ReadFileChunksAsync(file, cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is HubException or IOException or InvalidOperationException or OperationCanceledException)
        {
            LogTransferAborted(logger, transferId, ex);
        }
        finally
        {
            await file.DisposeAsync();
        }
    }

    private async Task PullFileAsync(
        HubConnection connection,
        UploadFileRequest request,
        string resolvedPath,
        CancellationToken cancellationToken)
    {
        var transferId = request.TransferId;
        string? error = null;
        try
        {
            await using var file = UploadFileHandler.OpenForWriting(resolvedPath, request.Overwrite);
            var chunks = connection.StreamAsync<byte[]>(
                ServerHubMethods.DownloadFileChunks, transferId, cancellationToken);
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                await file.WriteAsync(chunk, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HubException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            LogTransferAborted(logger, transferId, ex);
            error = ex.Message;
        }

        try
        {
            await connection.InvokeAsync(
                ServerHubMethods.CompleteTransfer, transferId, error, cancellationToken);
        }
        catch (Exception ex) when (ex is HubException or IOException or InvalidOperationException or OperationCanceledException)
        {
            LogTransferAborted(logger, transferId, ex);
        }
    }

    /// <summary>Reads the file in chunks. Closing it belongs to <see cref="PumpFileAsync"/>.</summary>
    private static async IAsyncEnumerable<byte[]> ReadFileChunksAsync(
        FileStream file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var buffer = new byte[DownloadChunkBytes];
            var read = await file.ReadAtLeastAsync(buffer, DownloadChunkBytes, throwOnEndOfStream: false, cancellationToken);
            if (read == 0)
            {
                yield break;
            }

            yield return read == DownloadChunkBytes ? buffer : buffer[..read];
        }
    }

    private async Task<string?> GetConnectionTokenAsync(Guid registrationId, TaskCompletionSource revoked)
    {
        var nonce = await serverApi.GetChallengeNonceAsync(registrationId, CancellationToken.None);
        if (nonce is null)
        {
            // The Server does not recognize the registration: this machine was unpaired.
            // Signal it and send no token — the doomed connection attempt that follows is
            // abandoned as soon as the outer loop sees the signal.
            revoked.TrySetResult();
            return null;
        }

        string signature;
        using (var key = identityStore.GetOrCreateKey())
        {
            signature = Convert.ToBase64String(
                key.SignData(Convert.FromBase64String(nonce), HashAlgorithmName.SHA256));
        }

        return await serverApi.GetConnectionTokenAsync(registrationId, signature, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay is not configured (Relay:ServerUrl is required); Daemon will not connect to a Server.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PAIRING CODE: {Code} — enter this code in a logged-in client to pair this machine (expires in 10 minutes).")]
    private static partial void LogPairingCode(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled: this machine is registration {RegistrationId}.")]
    private static partial void LogEnrolled(ILogger logger, Guid registrationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment attempt failed; retrying in {RetrySeconds}s.")]
    private static partial void LogEnrollmentRetry(ILogger logger, double retrySeconds, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Server {ServerUrl} as registration {RegistrationId}.")]
    private static partial void LogConnected(ILogger logger, Uri serverUrl, Guid registrationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect to Server {ServerUrl}; retrying in {RetrySeconds}s.")]
    private static partial void LogConnectFailed(ILogger logger, Uri serverUrl, double retrySeconds, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File transfer {TransferId} aborted.")]
    private static partial void LogTransferAborted(ILogger logger, Guid transferId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "This machine was unpaired on the Server; discarding the registration and starting fresh enrollment.")]
    private static partial void LogRevoked(ILogger logger);
}
