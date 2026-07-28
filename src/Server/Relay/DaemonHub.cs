using Microsoft.AspNetCore.SignalR;

namespace AngstromCommander.Server.Relay;

/// <summary>
/// The endpoint Daemons dial OUT to and hold open; the Server pushes relay requests
/// back down these connections. Identification by query string is a walking-skeleton
/// placeholder — keypair authentication arrives with enrollment.
/// </summary>
internal sealed class DaemonHub(IDaemonConnectionRegistry registry) : Hub
{
    internal const string DaemonIdQueryParameter = "daemonId";

    public override Task OnConnectedAsync()
    {
        var daemonId = this.GetDaemonId();
        if (daemonId is null)
        {
            this.Context.Abort();
        }
        else
        {
            registry.Register(daemonId, this.Context.ConnectionId);
        }

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var daemonId = this.GetDaemonId();
        if (daemonId is not null)
        {
            registry.Unregister(daemonId, this.Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    private string? GetDaemonId()
    {
        var daemonId = this.Context.GetHttpContext()?.Request.Query[DaemonIdQueryParameter].ToString();
        return string.IsNullOrWhiteSpace(daemonId) ? null : daemonId;
    }
}
