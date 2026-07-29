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
internal sealed class DaemonHub(IDaemonConnectionRegistry registry, AppDbContext db) : Hub
{
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
