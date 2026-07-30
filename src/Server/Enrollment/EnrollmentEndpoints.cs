using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
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
            static async Task<Results<Ok<PairingCodeResponse>, ProblemHttpResult>> (
                RequestCodeRequest request,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                if (!PublicKeys.TryImportSpki(request.PublicKeySpki, out var key))
                {
                    return TypedResults.Problem(
                        detail: "publicKeySpki is not a valid base64 SubjectPublicKeyInfo.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                key.Dispose();

                // One live code per machine: a restarted Daemon polls only its newest code,
                // so older unclaimed ones must stop being claimable (they'd pair to nobody).
                // Expired unclaimed codes are swept opportunistically at the same time.
                var now = DateTimeOffset.UtcNow;
                await db.PairingCodes
                    .Where(c => c.ClaimedRegistrationId == null
                        && (c.PublicKeySpki == request.PublicKeySpki || c.ExpiresAt < now))
                    .ExecuteDeleteAsync(cancellationToken);

                var code = new PairingCode
                {
                    Code = PairingCodeGenerator.Generate(),
                    PublicKeySpki = request.PublicKeySpki,
                    Platform = request.Platform,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime),
                };
                db.PairingCodes.Add(code);
                await db.SaveChangesAsync(cancellationToken);

                return TypedResults.Ok(new PairingCodeResponse(code.Code, code.ExpiresAt));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Polled by the enrolling Daemon while its code is displayed to the user.
        app.MapGet(
            "/api/enrollment/status/{code}",
            static async Task<Results<Ok<EnrollmentStatusResponse>, NoContent, NotFound>> (
                string code,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var pairingCode = await db.PairingCodes.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
                if (pairingCode is null || (pairingCode.ClaimedRegistrationId is null && pairingCode.ExpiresAt < DateTimeOffset.UtcNow))
                {
                    return TypedResults.NotFound();
                }

                return pairingCode.ClaimedRegistrationId is Guid registrationId
                    ? TypedResults.Ok(new EnrollmentStatusResponse(registrationId))
                    : TypedResults.NoContent();
            });

        // Called by a logged-in user typing in the code the Daemon displayed.
        app.MapPost(
            "/api/enrollment/claim",
            static async Task<Results<Ok<ClaimResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
                ClaimRequest request,
                HttpContext http,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var userId = Guid.Parse(http.User.FindFirst(AuthClaims.Subject)!.Value);

                // A JWT stays valid after its account is gone (e.g. wiped dev DB, deleted user);
                // that's an auth failure, not a 500-worthy FK violation.
                if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
                {
                    return TypedResults.Unauthorized();
                }

                var pairingCode = await db.PairingCodes
                    .FirstOrDefaultAsync(c => c.Code == request.Code, cancellationToken);
                if (pairingCode is null
                    || pairingCode.ClaimedRegistrationId is not null
                    || pairingCode.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    return TypedResults.Problem(
                        detail: "Unknown, expired or already-used pairing code.",
                        statusCode: StatusCodes.Status404NotFound);
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

                return TypedResults.Ok(new ClaimResponse(registration.Id, registration.DisplayName));
            })
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }
}

internal sealed record RequestCodeRequest(string PublicKeySpki, string Platform);

internal sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);

internal sealed record EnrollmentStatusResponse(Guid RegistrationId);

internal sealed record ClaimRequest(string Code, string DisplayName);

internal sealed record ClaimResponse(Guid RegistrationId, string DisplayName);
