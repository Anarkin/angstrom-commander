using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AngstromCommander.Server.Relay;

/// <summary>
/// In-flight downloads: each transfer is a bounded channel the Daemon's chunk stream is
/// written into and the HTTP response is read out of — the file itself is never held in
/// memory beyond the channel's few buffered chunks (backpressure does the rest).
/// Per-replica state like the connection registry; a cross-replica story arrives with it
/// (see ARCHITECTURE.md § Scaling).
/// </summary>
internal sealed class FileTransferRegistry
{
    private const int MaxBufferedChunks = 16;

    private readonly ConcurrentDictionary<Guid, PendingTransfer> _transfers = new();

    public Guid Create(Guid registrationId)
    {
        var transferId = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxBufferedChunks)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        this._transfers[transferId] = new PendingTransfer(registrationId, channel);
        return transferId;
    }

    /// <summary>Only the Daemon the transfer was requested from may write into it.</summary>
    public bool TryGet(Guid transferId, Guid registrationId, out Channel<byte[]> channel)
    {
        channel = null!;
        if (!this._transfers.TryGetValue(transferId, out var transfer) || transfer.RegistrationId != registrationId)
        {
            return false;
        }

        channel = transfer.Channel;
        return true;
    }

    public bool TryGetChannel(Guid transferId, out Channel<byte[]> channel)
    {
        channel = null!;
        if (!this._transfers.TryGetValue(transferId, out var transfer))
        {
            return false;
        }

        channel = transfer.Channel;
        return true;
    }

    public void Remove(Guid transferId)
    {
        if (this._transfers.TryRemove(transferId, out var transfer))
        {
            transfer.Channel.Writer.TryComplete();
        }
    }

    private sealed record PendingTransfer(Guid RegistrationId, Channel<byte[]> Channel);
}
