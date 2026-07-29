using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Daemon.Identity;
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

        var registrationId = identityStore.LoadRegistrationId()
            ?? await this.EnrollAsync(stoppingToken);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(relayOptions.ServerUrl, "/hub/daemon"), hubOptions =>
                hubOptions.AccessTokenProvider = () => this.GetConnectionTokenAsync(registrationId))
            .WithAutomaticReconnect()
            .Build();

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
                    // streaming invocation tagged with the transfer id. The iterator owns
                    // and disposes the stream.
                    var chunks = ReadFileChunksAsync(file, stoppingToken);
                    _ = this.PumpFileAsync(connection, request.TransferId, chunks, stoppingToken);
                }

                return response;
            });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                LogConnected(logger, relayOptions.ServerUrl, registrationId);
                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TimeoutException)
            {
                LogConnectFailed(logger, relayOptions.ServerUrl, RetryDelay.TotalSeconds, ex);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        try
        {
            // Stay alive until shutdown; WithAutomaticReconnect keeps the socket healthy.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
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

    private async Task PumpFileAsync(HubConnection connection, Guid transferId, IAsyncEnumerable<byte[]> chunks, CancellationToken cancellationToken)
    {
        try
        {
            await connection.InvokeAsync(ServerHubMethods.UploadFileChunks, transferId, chunks, cancellationToken);
        }
        catch (Exception ex) when (ex is HubException or IOException or InvalidOperationException or OperationCanceledException)
        {
            LogTransferAborted(logger, transferId, ex);
        }
    }

    private static async IAsyncEnumerable<byte[]> ReadFileChunksAsync(
        FileStream file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using (file)
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
    }

    private async Task<string?> GetConnectionTokenAsync(Guid registrationId)
    {
        var nonce = await serverApi.GetChallengeNonceAsync(registrationId, CancellationToken.None);
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
}
