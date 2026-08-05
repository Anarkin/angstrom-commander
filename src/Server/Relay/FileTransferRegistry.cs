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
        if (this._transfers.Count(entry => entry.Value.UserId == userId) >= limits.Value.ConcurrentTransfersPerUser)
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

        // Something moved. Recorded after the quota check, so a chunk that broke the quota
        // (and abandoned the transfer) never counts as progress.
        transfer.Progress.Bump();

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

    /// <summary>
    /// Awaited by the HTTP side of an upload or a machine-to-machine copy: the error the
    /// receiver reported, or null when the bytes are on disk.
    /// </summary>
    /// <remarks>
    /// The deadline is a stall deadline, not a duration cap: it restarts every time a chunk
    /// moves, so a transfer only fails when it has gone silent for a whole window. Bounding
    /// the *whole* copy at one window instead would kill every transfer slower than it — a
    /// 300 MB copy over a home uplink — and, because the endpoint's cleanup ends the channel,
    /// the receiving Daemon would read that as a clean end of file and keep the truncation.
    /// </remarks>
    public async Task<string?> WaitForCompletionAsync(
        Guid transferId, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        if (!this._transfers.TryGetValue(transferId, out var transfer))
        {
            return "The transfer is no longer known.";
        }

        var seen = transfer.Progress.Value;
        while (true)
        {
            try
            {
                return await transfer.Completion.Task.WaitAsync(stallTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                var moved = transfer.Progress.Value;
                if (moved == seen)
                {
                    throw;
                }

                seen = moved;
            }
        }
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

    /// <summary>
    /// Drops a transfer once its endpoint is done with it — on the way out of every transfer
    /// endpoint, success or failure alike.
    /// </summary>
    /// <remarks>
    /// The channel is error-completed rather than completed, for the same reason
    /// <see cref="AbandonFor"/> is: a plain completion reads to the receiving Daemon as a
    /// clean end of file, so an endpoint bailing out early — a stall, a cancelled request —
    /// would leave a truncated file behind reported as a whole one. On the success path this
    /// changes nothing: whoever wrote the last chunk has already completed the channel, and
    /// completing a completed channel is a no-op.
    /// </remarks>
    public void Remove(Guid transferId)
    {
        this.Abandon(transferId, "The transfer ended before it was complete.");
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

    /// <summary>What today's metering has already charged the user; zero once the day rolls.</summary>
    public long BytesUsedToday(Guid userId)
    {
        if (!this._usage.TryGetValue(userId, out var usage))
        {
            return 0;
        }

        lock (usage)
        {
            return usage.Day == this.Today ? usage.BytesToday : 0;
        }
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

        public ProgressMarker Progress { get; } = new();
    }

    /// <summary>
    /// How many chunks a transfer has moved. Never read as a quantity — only compared with
    /// its own earlier value to answer "did anything move while I was waiting", which is what
    /// separates a slow transfer from a dead one.
    /// </summary>
    private sealed class ProgressMarker
    {
        private long _chunks;

        public long Value => Interlocked.Read(ref this._chunks);

        public void Bump()
        {
            Interlocked.Increment(ref this._chunks);
        }
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
