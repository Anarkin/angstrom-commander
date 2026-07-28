using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Daemon.Relay;

/// <summary>
/// Holds the Daemon's single persistent outbound connection to the Server open and
/// answers the relay requests the Server pushes down it.
/// </summary>
internal sealed partial class DaemonRelayService(
    IOptions<RelayOptions> options,
    ListDirectoryHandler listDirectoryHandler,
    ILogger<DaemonRelayService> logger) : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var relayOptions = options.Value;
        if (relayOptions.ServerUrl is null || string.IsNullOrWhiteSpace(relayOptions.DaemonId))
        {
            LogNotConfigured(logger);
            return;
        }

        var hubUrl = new Uri(
            relayOptions.ServerUrl,
            $"/hub/daemon?daemonId={Uri.EscapeDataString(relayOptions.DaemonId)}");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        connection.On<ListDirectoryRequest, ListDirectoryResponse>(
            DaemonHubMethods.ListDirectory,
            request => listDirectoryHandler.Handle(request));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                LogConnected(logger, relayOptions.ServerUrl, relayOptions.DaemonId);
                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TimeoutException)
            {
                LogConnectFailed(logger, relayOptions.ServerUrl, ConnectRetryDelay.TotalSeconds, ex);
                await Task.Delay(ConnectRetryDelay, stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay is not configured (Relay:ServerUrl and Relay:DaemonId are required); Daemon will not connect to a Server.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Server {ServerUrl} as Daemon '{DaemonId}'.")]
    private static partial void LogConnected(ILogger logger, Uri serverUrl, string daemonId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect to Server {ServerUrl}; retrying in {RetrySeconds}s.")]
    private static partial void LogConnectFailed(ILogger logger, Uri serverUrl, double retrySeconds, Exception exception);
}
