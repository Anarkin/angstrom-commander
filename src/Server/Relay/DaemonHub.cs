using System.Runtime.CompilerServices;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Relay;

/// <summary>
/// The endpoint Daemons dial OUT to and hold open; the Server pushes relay requests
/// back down these connections. A Daemon gets here only with a connection token it
/// earned by signing a challenge with its registered private key.
/// </summary>
[Authorize(Policy = AuthPolicies.Daemon)]
internal sealed class DaemonHub(IDaemonConnectionRegistry registry, FileTransferRegistry transfers, AppDbContext db) : Hub
{
    private const int MaxChunkBytes = 256 * 1024;

    /// <summary>
    /// Client-to-server streaming: the Daemon pushes a requested file's bytes, chunk by
    /// chunk, into the transfer's channel. The bounded channel gives backpressure — if
    /// the downloading client reads slowly, the Daemon's stream slows with it.
    /// </summary>
    public async Task UploadFileChunks(Guid transferId, IAsyncEnumerable<byte[]> chunks)
    {
        var registrationId = this.GetRegistrationId();
        if (registrationId is null || !transfers.TryGetForWriter(transferId, registrationId.Value, out var channel))
        {
            throw new HubException("Unknown transfer.");
        }

        try
        {
            // No ConnectionAborted here: SignalR already terminates the incoming stream
            // (with an error) when the connection drops, and long-polling briefly trips
            // that token between polls, which would abort healthy transfers.
            await foreach (var chunk in chunks)
            {
                if (chunk.Length > MaxChunkBytes)
                {
                    throw new HubException("Chunk too large.");
                }

                // Quota + bandwidth, charged to the transfer's owner. Crossing the quota
                // abandons the transfer, so the reader learns why instead of seeing a
                // truncated file end quietly.
                await transfers.MeterAsync(transferId, chunk.Length, CancellationToken.None);
                await channel.Writer.WriteAsync(chunk);
            }

            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            throw;
        }
    }

    /// <summary>
    /// Server-to-client streaming: the receiving Daemon pulls the bytes of a file being sent to
    /// it. SignalR cannot push a stream to a client, so the Daemon initiates this itself after
    /// being told a transfer is waiting. Backpressure is the same bounded channel in reverse —
    /// a Daemon writing slowly to disk slows the sender.
    /// </summary>
    public async IAsyncEnumerable<byte[]> DownloadFileChunks(
        Guid transferId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var registrationId = this.GetRegistrationId();
        if (registrationId is null || !transfers.TryGetForReader(transferId, registrationId.Value, out var channel))
        {
            throw new HubException("Unknown transfer.");
        }

        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>Reported by a receiving Daemon once it has finished writing (or failed).</summary>
    public void CompleteTransfer(Guid transferId, string? error)
    {
        var registrationId = this.GetRegistrationId();
        if (registrationId is null || !transfers.TryComplete(transferId, registrationId.Value, error))
        {
            throw new HubException("Unknown transfer.");
        }
    }

    public override async Task OnConnectedAsync()
    {
        var registrationId = this.GetRegistrationId();
        if (registrationId is null)
        {
            this.Context.Abort();
        }
        else
        {
            registry.Register(registrationId.Value.ToString(), this.Context.ConnectionId);
            await this.TouchLastSeenAsync(registrationId.Value);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var registrationId = this.GetRegistrationId();
        if (registrationId is not null)
        {
            registry.Unregister(registrationId.Value.ToString(), this.Context.ConnectionId);

            // Whatever this machine was sending or receiving is not going to finish.
            transfers.AbandonFor(registrationId.Value);
            await this.TouchLastSeenAsync(registrationId.Value);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Guid? GetRegistrationId()
    {
        var claim = this.Context.User?.FindFirst(AuthClaims.DaemonRegistrationId)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private async Task TouchLastSeenAsync(Guid registrationId)
    {
        var now = DateTimeOffset.UtcNow;
        await db.DaemonRegistrations
            .Where(r => r.Id == registrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(static r => r.LastSeenAt, now));
    }
}
