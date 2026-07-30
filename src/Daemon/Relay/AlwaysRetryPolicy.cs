using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;

namespace AngstromCommander.Daemon.Relay;

/// <summary>
/// Reconnect forever, with a backoff that settles into a steady poll. SignalR's stock policy
/// tries four times over about 40 seconds and then gives up for good — shorter than a Server
/// deploy, after which the Daemon would sit there alive, healthy, and unreachable until someone
/// restarted it by hand. A Daemon holding its socket open is the whole "no port forwarding"
/// promise, so giving up is never the right answer.
/// </summary>
internal sealed class AlwaysRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount == 0)
        {
            // A dropped socket is usually a blip; try again before anyone notices.
            return TimeSpan.Zero;
        }

        var seconds = Math.Min(Math.Pow(2, retryContext.PreviousRetryCount), MaxDelay.TotalSeconds);

        // Spread the retries out: every Daemon in the fleet is reconnecting from the same
        // Server restart, and they should not all come back in the same instant.
        var jitterPercent = RandomNumberGenerator.GetInt32(80, 121);
        return TimeSpan.FromSeconds(seconds * jitterPercent / 100.0);
    }
}
