using AngstromCommander.Protocol;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Relay;

internal static class RelayEndpoints
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's machine list, with live connection status.
        app.MapGet(
            "/api/daemons",
            static async (HttpContext http, AppDbContext db, IDaemonConnectionRegistry registry, CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                var registrations = await db.DaemonRegistrations.AsNoTracking()
                    .Where(r => r.UserId == userId && r.RevokedAt == null)
                    .OrderBy(static r => r.CreatedAt)
                    .ToListAsync(cancellationToken);

                return Results.Ok(registrations.Select(r => new
                {
                    registrationId = r.Id,
                    displayName = r.DisplayName,
                    platform = r.Platform,
                    createdAt = r.CreatedAt,
                    lastSeenAt = r.LastSeenAt,
                    online = registry.TryGetConnection(r.Id.ToString(), out _),
                }));
            })
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/daemons/{registrationId:guid}/list",
            static async (
                Guid registrationId,
                string path,
                HttpContext http,
                AppDbContext db,
                IDaemonConnectionRegistry registry,
                IHubContext<DaemonHub> hub,
                CancellationToken cancellationToken) =>
            {
                // Authorize before routing: the registration must belong to the caller.
                var userId = GetUserId(http);
                var owned = await db.DaemonRegistrations.AsNoTracking().AnyAsync(
                    r => r.Id == registrationId && r.UserId == userId && r.RevokedAt == null,
                    cancellationToken);
                if (!owned)
                {
                    return Results.NotFound(new { error = "No such machine." });
                }

                if (!registry.TryGetConnection(registrationId.ToString(), out var connectionId))
                {
                    return Results.Problem(
                        detail: "The machine is not connected right now.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(OperationTimeout);

                ListDirectoryResponse response;
                try
                {
                    response = await hub.Clients.Client(connectionId)
                        .InvokeAsync<ListDirectoryResponse>(
                            DaemonHubMethods.ListDirectory,
                            new ListDirectoryRequest(path),
                            timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return Results.Problem(
                        detail: "The machine did not answer.",
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }

                return response.Error is null
                    ? Results.Ok(response.Entries)
                    : Results.BadRequest(new { error = response.Error });
            })
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }

    private static Guid GetUserId(HttpContext http)
    {
        return Guid.Parse(http.User.FindFirst(AuthClaims.Subject)!.Value);
    }
}
