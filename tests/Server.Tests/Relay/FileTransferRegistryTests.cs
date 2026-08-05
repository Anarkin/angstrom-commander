using System.Diagnostics;
using AngstromCommander.Server.Relay;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Server.Tests.Relay;

public class FileTransferRegistryTests
{
    private static readonly Guid Sender = Guid.NewGuid();
    private static readonly Guid Receiver = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    [Fact]
    public void OnlyTheMachineAskedToSendMayWrite()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        Assert.True(registry.TryGetForWriter(transferId, Sender, out _));
        Assert.False(registry.TryGetForWriter(transferId, Receiver, out _));
        Assert.False(registry.TryGetForWriter(transferId, Stranger, out _));
    }

    [Fact]
    public void OnlyTheMachineAskedToReceiveMayRead()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        Assert.True(registry.TryGetForReader(transferId, Receiver, out _));
        Assert.False(registry.TryGetForReader(transferId, Sender, out _));
        Assert.False(registry.TryGetForReader(transferId, Stranger, out _));
    }

    [Fact]
    public void OnlyTheReceiverMayReportCompletion()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        Assert.False(registry.TryComplete(transferId, Stranger, error: null));
        Assert.True(registry.TryComplete(transferId, Receiver, error: null));
    }

    [Fact]
    public void AnUnknownTransferIsNobodys()
    {
        var registry = CreateRegistry();

        Assert.False(registry.TryGetForWriter(Guid.NewGuid(), Sender, out _));
        Assert.False(registry.TryGetForReader(Guid.NewGuid(), Receiver, out _));
        Assert.False(registry.TryGetChannel(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task ADisconnectingReceiverReleasesTheWaitingSender()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: null, readerRegistrationId: Receiver);
        var completion = registry.WaitForCompletionAsync(
            transferId, TimeSpan.FromSeconds(5), CancellationToken.None);

        registry.AbandonFor(Receiver);

        Assert.NotNull(await completion);
    }

    [Fact]
    public async Task ADisconnectingSenderFailsTheStreamRatherThanEndingIt()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: null);
        Assert.True(registry.TryGetChannel(transferId, out var channel));

        registry.AbandonFor(Sender);

        // A plain completion would read as a clean end of file, and the caller would report a
        // truncated download as a success.
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                Assert.Empty(chunk);
            }
        });
    }

    [Fact]
    public async Task RemovingATransferFailsTheStreamRatherThanEndingIt()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);
        Assert.True(registry.TryGetChannel(transferId, out var channel));

        // Remove is what every transfer endpoint calls on its way out — including the ways out
        // that are failures. Ending the channel cleanly there tells the receiving Daemon the
        // file is complete, so a copy cut short mid-stream is written to disk under its final
        // name and reported as a success. Only an error reaches the receiver as a failure.
        registry.Remove(transferId);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                Assert.Empty(chunk);
            }
        });
    }

    [Fact]
    public async Task ATransferStillMovingIsNotTimedOut()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        // The deadline is a stall deadline, not a duration cap: this transfer runs three times
        // longer than one window and must survive, because it never stops moving. A cap here
        // would fail every copy slower than the window — on the default bandwidth ceiling, any
        // copy over about 750 MB — and hand the target a truncated file besides.
        var stallTimeout = TimeSpan.FromMilliseconds(500);
        var waiting = registry.WaitForCompletionAsync(transferId, stallTimeout, CancellationToken.None);

        for (var chunk = 0; chunk < 15; chunk++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await registry.MeterAsync(transferId, 1, CancellationToken.None);
        }

        Assert.True(registry.TryComplete(transferId, Receiver, error: null));
        Assert.Null(await waiting);
    }

    [Fact]
    public async Task ATransferThatGoesSilentTimesOut()
    {
        var registry = CreateRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        // The other half of the same rule: no bytes for a whole window is a dead transfer, and
        // the endpoint has to be let go rather than hold the caller open forever.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            registry.WaitForCompletionAsync(transferId, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public void TheConcurrencyCeilingIsConfigurable()
    {
        // Environments size their own limits; this one was a const long after the quota and
        // bandwidth caps became options, so the docs promised a knob that did not exist.
        var registry = CreateRegistry(concurrentTransfersPerUser: 2);
        var user = Guid.NewGuid();

        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out _));
        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out _));
        Assert.Equal(
            CreateTransferResult.TooManyConcurrent,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out _));
    }

    [Fact]
    public void AbandoningLeavesOtherMachinesTransfersAlone()
    {
        var registry = CreateRegistry();
        var mine = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);
        var theirs = Create(registry, writerRegistrationId: Stranger, readerRegistrationId: null);

        registry.AbandonFor(Sender);

        Assert.False(registry.TryGetChannel(mine, out _));
        Assert.True(registry.TryGetChannel(theirs, out _));
    }

    [Fact]
    public void OneUserCannotOpenTransfersWithoutEnd()
    {
        // Every live transfer holds a buffer, on a Server shared with everyone else's machines.
        var registry = CreateRegistry();
        var greedy = Guid.NewGuid();

        var opened = Enumerable.Range(0, 64)
            .Select(attempt =>
                registry.TryCreate(greedy, writerRegistrationId: Sender, readerRegistrationId: null, out _))
            .ToList();

        Assert.Contains(CreateTransferResult.TooManyConcurrent, opened);

        // And one user filling up says nothing about anybody else.
        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(Guid.NewGuid(), writerRegistrationId: Sender, readerRegistrationId: null, out _));
    }

    [Fact]
    public void FinishedTransfersFreeTheirPlaceInTheAllowance()
    {
        var registry = CreateRegistry();
        var user = Guid.NewGuid();
        var opened = new List<Guid>();
        while (registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out var transferId)
            == CreateTransferResult.Created)
        {
            opened.Add(transferId);
        }

        registry.Remove(opened[0]);

        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out _));
    }

    [Fact]
    public async Task CrossingTheDailyQuotaFailsTheTransferAndBlocksNewOnes()
    {
        var registry = CreateRegistry(dailyBytesPerUser: 100);
        var user = Guid.NewGuid();
        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out var transferId));

        // Within the allowance: fine. The byte that crosses it: the transfer dies with the
        // quota's own message, so the failure explains itself. (The completion task is taken
        // up front, the way the copy endpoint holds it while bytes flow.)
        var completion = registry.WaitForCompletionAsync(
            transferId, TimeSpan.FromSeconds(5), CancellationToken.None);
        await registry.MeterAsync(transferId, 100, CancellationToken.None);
        await Assert.ThrowsAsync<TransferQuotaExceededException>(
            () => registry.MeterAsync(transferId, 1, CancellationToken.None));
        Assert.False(registry.TryGetChannel(transferId, out _));
        Assert.Equal(TransferQuotaExceededException.UserFacingMessage, await completion);

        // And no new transfer starts while the allowance is spent — but only for that user.
        Assert.Equal(
            CreateTransferResult.QuotaExhausted,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out _));
        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(Guid.NewGuid(), writerRegistrationId: Sender, readerRegistrationId: null, out _));
    }

    [Fact]
    public async Task TheQuotaResetsAtMidnightUtc()
    {
        var clock = new SettableClock(new DateTimeOffset(2026, 8, 3, 23, 0, 0, TimeSpan.Zero));
        var registry = CreateRegistry(dailyBytesPerUser: 100, clock: clock);
        var user = Guid.NewGuid();
        registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out var transferId);
        await registry.MeterAsync(transferId, 100, CancellationToken.None);
        Assert.Equal(
            CreateTransferResult.QuotaExhausted,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out _));

        clock.UtcNow = new DateTimeOffset(2026, 8, 4, 0, 0, 1, TimeSpan.Zero);

        Assert.Equal(
            CreateTransferResult.Created,
            registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out var fresh));
        await registry.MeterAsync(fresh, 100, CancellationToken.None);
    }

    [Fact]
    public async Task TheBandwidthCapSlowsAnOverdrawnSender()
    {
        // 100 KB/s, and the burst allowance is one second's worth — so pushing 150 KB overdraws
        // by 50 KB and the second call must sleep about half a second paying it off.
        var registry = CreateRegistry(bytesPerSecondPerUser: 100_000);
        registry.TryCreate(
            Guid.NewGuid(), writerRegistrationId: Sender, readerRegistrationId: null, out var transferId);

        var stopwatch = Stopwatch.StartNew();
        await registry.MeterAsync(transferId, 150_000, CancellationToken.None);
        await registry.MeterAsync(transferId, 1, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400),
            $"Expected the overdraft to cost ~500ms; it cost {stopwatch.Elapsed.TotalMilliseconds}ms.");
    }

    [Fact]
    public async Task DisabledLimitsMeterNothing()
    {
        var registry = CreateRegistry(dailyBytesPerUser: 0, bytesPerSecondPerUser: 0);
        var user = Guid.NewGuid();
        registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null, out var transferId);

        var stopwatch = Stopwatch.StartNew();
        await registry.MeterAsync(transferId, int.MaxValue, CancellationToken.None);
        await registry.MeterAsync(transferId, int.MaxValue, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(registry.TryGetChannel(transferId, out _));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static Guid Create(FileTransferRegistry registry, Guid? writerRegistrationId, Guid? readerRegistrationId)
    {
        var result = registry.TryCreate(Guid.NewGuid(), writerRegistrationId, readerRegistrationId, out var transferId);
        Assert.Equal(CreateTransferResult.Created, result);
        return transferId;
    }

    private static FileTransferRegistry CreateRegistry(
        long dailyBytesPerUser = 0,
        long bytesPerSecondPerUser = 0,
        int concurrentTransfersPerUser = 16,
        TimeProvider? clock = null)
    {
        // Limits default to off here so the pre-existing behavioural tests stay about
        // what they were about; the quota/bandwidth tests opt in explicitly.
        var options = Options.Create(new TransferLimitOptions
        {
            DailyBytesPerUser = dailyBytesPerUser,
            BytesPerSecondPerUser = bytesPerSecondPerUser,
            ConcurrentTransfersPerUser = concurrentTransfersPerUser,
        });
        return new FileTransferRegistry(options, clock ?? TimeProvider.System);
    }

    /// <summary>Only the wall clock is faked; timers stay real (no test here sleeps on one).</summary>
    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow()
        {
            return this.UtcNow;
        }
    }
}
