using AngstromCommander.Daemon.Relay;
using Microsoft.AspNetCore.SignalR.Client;

namespace AngstromCommander.Daemon.Tests.Relay;

public class AlwaysRetryPolicyTests
{
    [Fact]
    public void RetriesImmediatelyTheFirstTime()
    {
        Assert.Equal(TimeSpan.Zero, new AlwaysRetryPolicy().NextRetryDelay(After(0)));
    }

    [Fact]
    public void NeverGivesUp()
    {
        var policy = new AlwaysRetryPolicy();

        // The failure this guards against is a Daemon that stops retrying and stays offline
        // until someone restarts it, so there is no attempt count that may return null.
        Assert.All(
            new long[] { 1, 5, 50, 5_000, long.MaxValue },
            attempts => Assert.NotNull(policy.NextRetryDelay(After(attempts))));
    }

    [Fact]
    public void BacksOffButStaysWithinHalfAMinute()
    {
        var policy = new AlwaysRetryPolicy();

        var early = policy.NextRetryDelay(After(1))!.Value;
        var later = policy.NextRetryDelay(After(4))!.Value;
        var muchLater = policy.NextRetryDelay(After(1_000))!.Value;

        Assert.True(early < later, $"expected {early} to be shorter than {later}");
        Assert.InRange(muchLater, TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(36));
    }

    private static RetryContext After(long previousRetryCount)
    {
        return new RetryContext { PreviousRetryCount = previousRetryCount };
    }
}
