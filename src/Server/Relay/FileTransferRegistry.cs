using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Server.Relay;

/// <summary>Why a transfer could not be created — the endpoint picks the status code.</summary>
internal enum CreateTransferResult
{
    Created,
    TooManyConcurrent,
    QuotaExhausted,
}

/// <summary>
/// In-flight transfers. Each is a bounded channel with exactly one writer and one reader, so the
/// file itself is never held in memory beyond a few buffered chunks (backpressure does the rest):
/// <list type="bullet">
/// <item>download — a Daemon writes, the HTTP response reads</item>
/// <item>upload — the HTTP request writes, a Daemon reads</item>
/// <item>machine-to-machine copy — one Daemon writes, another reads</item>
/// </list>
/// Either end may be a Daemon, so each side's registration is recorded and checked. Per-replica
/// state like the connection registry; a cross-replica story arrives with it (ARCHITECTURE.md § Scaling).
/// </summary>
internal sealed class FileTransferRegistry(IOptions<TransferLimitOptions> limits, TimeProvider clock)
{
    private const int MaxBufferedChunks = 16;
    private const int MaxTransfersPerUser = 16;

    private readonly ConcurrentDictionary<Guid, PendingTransfer> _transfers = new();
    private readonly ConcurrentDictionary<Guid, UserUsage> _usage = new();

    /// <summary>
    /// Registers a transfer, or says why it will not: the user already has as many in flight as
    /// they are allowed, or their daily byte quota is spent. A null registration id means that
    /// side is the HTTP request/response rather than a Daemon.
    /// </summary>
    /// <remarks>
    /// The concurrency cap is what keeps memory bounded: every live transfer holds a bounded
    /// channel, so unlimited transfers is unlimited memory on a Server shared with everyone
    /// else's machines. Counting and inserting are not one atomic step, so a burst of
    /// simultaneous requests can land a little over the line — near enough for a resource
    /// guard, and far from unbounded.
    /// </remarks>
    public CreateTransferResult TryCreate(
        Guid userId, Guid? writerRegistrationId, Guid? readerRegistrationId, out Guid transferId)
    {
        transferId = Guid.Empty;
        if (this._transfers.Count(entry => entry.Value.UserId == userId) >= MaxTransfersPerUser)
        {
            return CreateTransferResult.TooManyConcurrent;
        }

        var quota = limits.Value.DailyBytesPerUser;
        if (quota > 0 && this._usage.TryGetValue(userId, out var usage))
        {
            lock (usage)
            {
                if (usage.Day == this.Today && usage.BytesToday >= quota)
                {
                    return CreateTransferResult.QuotaExhausted;
                }
            }
        }

        transferId = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxBufferedChunks)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        this._transfers[transferId] = new PendingTransfer(
            userId, writerRegistrationId, readerRegistrationId, channel);
        return CreateTransferResult.Created;
    }

    /// <summary>
    /// Counts a chunk against the owner's daily quota and slows the caller to the configured
    /// bandwidth. Call it once per chunk, before the chunk enters the transfer's channel —
    /// between them the two call sites (the hub's incoming stream and the upload body pump)
    /// see every relayed byte exactly once. Crossing the quota abandons the transfer and
    /// throws <see cref="TransferQuotaExceededException"/>.
    /// </summary>
    public async Task MeterAsync(Guid transferId, int bytes, CancellationToken cancellationToken)
    {
        // A transfer that is already gone meters nothing; its bytes are going nowhere.
        if (!this._transfers.TryGetValue(transferId, out var transfer))
        {
            return;
        }

        var usage = this._usage.GetOrAdd(transfer.UserId, static _ => new UserUsage());
        TimeSpan wait;
        lock (usage)
        {
            var now = clock.GetUtcNow();
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (usage.Day != day)
            {
                usage.Day = day;
                usage.BytesToday = 0;
            }

            usage.BytesToday += bytes;
            var quota = limits.Value.DailyBytesPerUser;
            if (quota > 0 && usage.BytesToday > quota)
            {
                this.Abandon(transferId, TransferQuotaExceededException.UserFacingMessage);
                throw new TransferQuotaExceededException();
            }

            // Debt-based token bucket: the chunk always passes, and when it overdraws the
            // balance the caller sleeps the debt off before sending the next one. Smooth,
            // and immune to chunks larger than one second's allowance.
            var rate = limits.Value.BytesPerSecondPerUser;
            if (rate <= 0)
            {
                wait = TimeSpan.Zero;
            }
            else
            {
                var elapsed = now - usage.LastRefill;
                usage.LastRefill = now;
                usage.TokenBalance = Math.Min(rate, usage.TokenBalance + (elapsed.TotalSeconds * rate)) - bytes;
                wait = usage.TokenBalance >= 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(-usage.TokenBalance / rate);
            }
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, clock, cancellationToken);
        }
    }

    /// <summary>Only the Daemon that was asked to send may write into the transfer.</summary>
    public bool TryGetForWriter(Guid transferId, Guid registrationId, out Channel<byte[]> channel)
    {
        return this.TryGet(transferId, transfer => transfer.WriterRegistrationId == registrationId, out channel);
    }

    /// <summary>Only the Daemon that was asked to receive may read from the transfer.</summary>
    public bool TryGetForReader(Guid transferId, Guid registrationId, out Channel<byte[]> channel)
    {
        return this.TryGet(transferId, transfer => transfer.ReaderRegistrationId == registrationId, out channel);
    }

    /// <summary>For the HTTP side, which is already authorized by the endpoint.</summary>
    public bool TryGetChannel(Guid transferId, out Channel<byte[]> channel)
    {
        return this.TryGet(transferId, static _ => true, out channel);
    }

    /// <summary>Awaited by the HTTP side of an upload: the error the receiver reported, or null.</summary>
    public Task<string?> Completion(Guid transferId)
    {
        return this._transfers.TryGetValue(transferId, out var transfer)
            ? transfer.Completion.Task
            : Task.FromResult<string?>("The transfer is no longer known.");
    }

    /// <summary>Reported by the receiving Daemon once it finished writing (or failed).</summary>
    public bool TryComplete(Guid transferId, Guid registrationId, string? error)
    {
        if (!this._transfers.TryGetValue(transferId, out var transfer)
            || transfer.ReaderRegistrationId != registrationId)
        {
            return false;
        }

        return transfer.Completion.TrySetResult(error);
    }

    public void Remove(Guid transferId)
    {
        if (this._transfers.TryRemove(transferId, out var transfer))
        {
            transfer.Channel.Writer.TryComplete();
            transfer.Completion.TrySetResult("The transfer was abandoned.");
        }
    }

    /// <summary>
    /// Fails every transfer a machine was to send or receive, because its connection went away.
    /// Without this the other side waits on bytes that will never arrive: an upload blocks on a
    /// full channel nobody is draining, and a download would otherwise end in what looks like a
    /// clean end of file — a truncated copy reported as a success.
    /// </summary>
    public void AbandonFor(Guid registrationId)
    {
        foreach (var (transferId, transfer) in this._transfers)
        {
            if (transfer.WriterRegistrationId == registrationId || transfer.ReaderRegistrationId == registrationId)
            {
                this.Abandon(transferId, "The machine disconnected during the transfer.");
            }
        }
    }

    private void Abandon(Guid transferId, string reason)
    {
        if (this._transfers.TryRemove(transferId, out var transfer))
        {
            transfer.Channel.Writer.TryComplete(new IOException(reason));
            transfer.Completion.TrySetResult(reason);
        }
    }

    private bool TryGet(Guid transferId, Func<PendingTransfer, bool> isAllowed, out Channel<byte[]> channel)
    {
        channel = null!;
        if (!this._transfers.TryGetValue(transferId, out var transfer) || !isAllowed(transfer))
        {
            return false;
        }

        channel = transfer.Channel;
        return true;
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private sealed record PendingTransfer(
        Guid UserId,
        Guid? WriterRegistrationId,
        Guid? ReaderRegistrationId,
        Channel<byte[]> Channel)
    {
        public TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Locked on itself; per-replica like everything else here.</summary>
    private sealed class UserUsage
    {
        public DateOnly Day { get; set; }

        public long BytesToday { get; set; }

        public double TokenBalance { get; set; }

        public DateTimeOffset LastRefill { get; set; }
    }
}
