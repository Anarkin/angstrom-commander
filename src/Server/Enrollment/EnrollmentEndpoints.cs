using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Enrollment;

internal static class EnrollmentEndpoints
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Called by an enrolling Daemon (it has no identity yet, so this is anonymous).
        app.MapPost(
            "/api/enrollment/code",
            static async (RequestCodeRequest request, AppDbContext db, CancellationToken cancellationToken) =>
            {
                if (!PublicKeys.TryImportSpki(request.PublicKeySpki, out var key))
                {
                    return Results.BadRequest(new { error = "publicKeySpki is not a valid base64 SubjectPublicKeyInfo." });
                }

                key.Dispose();

                var code = new PairingCode
                {
                    Code = PairingCodeGenerator.Generate(),
                    PublicKeySpki = request.PublicKeySpki,
                    Platform = request.Platform,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime),
                };
                db.PairingCodes.Add(code);
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { code = code.Code, expiresAt = code.ExpiresAt });
            });

        // Polled by the enrolling Daemon while its code is displayed to the user.
        app.MapGet(
            "/api/enrollment/status/{code}",
            static async (string code, AppDbContext db, CancellationToken cancellationToken) =>
            {
                var pairingCode = await db.PairingCodes.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
                if (pairingCode is null || (pairingCode.ClaimedRegistrationId is null && pairingCode.ExpiresAt < DateTimeOffset.UtcNow))
                {
                    return Results.NotFound();
                }

                return pairingCode.ClaimedRegistrationId is Guid registrationId
                    ? Results.Ok(new { registrationId })
                    : Results.NoContent();
            });

        // Called by a logged-in user typing in the code the Daemon displayed.
        app.MapPost(
            "/api/enrollment/claim",
            static async (ClaimRequest request, HttpContext http, AppDbContext db, CancellationToken cancellationToken) =>
            {
                var userId = Guid.Parse(http.User.FindFirst(AuthClaims.Subject)!.Value);
                var pairingCode = await db.PairingCodes
                    .FirstOrDefaultAsync(c => c.Code == request.Code, cancellationToken);
                if (pairingCode is null
                    || pairingCode.ClaimedRegistrationId is not null
                    || pairingCode.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    return Results.NotFound(new { error = "Unknown, expired or already-used pairing code." });
                }

                var registration = new DaemonRegistration
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    DisplayName = request.DisplayName,
                    PublicKeySpki = pairingCode.PublicKeySpki,
                    Platform = pairingCode.Platform,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.DaemonRegistrations.Add(registration);
                pairingCode.ClaimedRegistrationId = registration.Id;
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { registrationId = registration.Id, displayName = registration.DisplayName });
            })
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }
}

internal sealed record RequestCodeRequest(string PublicKeySpki, string Platform);

internal sealed record ClaimRequest(string Code, string DisplayName);
