using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AngstromCommander.Server.Relay;

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
internal sealed class FileTransferRegistry
{
    private const int MaxBufferedChunks = 16;
    private const int MaxTransfersPerUser = 16;

    private readonly ConcurrentDictionary<Guid, PendingTransfer> _transfers = new();

    /// <summary>
    /// Registers a transfer, or returns null when the user already has as many in flight as they
    /// are allowed. A null registration id means that side is the HTTP request/response rather
    /// than a Daemon.
    /// </summary>
    /// <remarks>
    /// The cap is what keeps memory bounded: every live transfer holds a bounded channel, so
    /// unlimited transfers is unlimited memory on a Server shared with everyone else's machines.
    /// Counting and inserting are not one atomic step, so a burst of simultaneous requests can
    /// land a little over the line — near enough for a resource guard, and far from unbounded.
    /// </remarks>
    public Guid? TryCreate(Guid userId, Guid? writerRegistrationId, Guid? readerRegistrationId)
    {
        if (this._transfers.Count(entry => entry.Value.UserId == userId) >= MaxTransfersPerUser)
        {
            return null;
        }

        var transferId = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxBufferedChunks)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        this._transfers[transferId] = new PendingTransfer(
            userId, writerRegistrationId, readerRegistrationId, channel);
        return transferId;
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

    private sealed record PendingTransfer(
        Guid UserId,
        Guid? WriterRegistrationId,
        Guid? ReaderRegistrationId,
        Channel<byte[]> Channel)
    {
        public TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
