using System.ComponentModel.DataAnnotations;
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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ValidatesRequest<RequestCodeRequest>()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

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
            })
            .RequireRateLimiting(RateLimitPolicies.EnrollmentPolling);

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

                var now = DateTimeOffset.UtcNow;
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                var pairingCode = await db.PairingCodes.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Code == request.Code, cancellationToken);
                if (pairingCode is null
                    || pairingCode.ClaimedRegistrationId is not null
                    || pairingCode.ExpiresAt < now)
                {
                    return UnusableCode();
                }

                var registration = new DaemonRegistration
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    DisplayName = request.DisplayName,
                    PublicKeySpki = pairingCode.PublicKeySpki,
                    Platform = pairingCode.Platform,
                    CreatedAt = now,
                };

                // The read above cannot decide this on its own: two requests racing on one code
                // both pass it, and the loser would pair the same machine a second time. Only the
                // request whose update still finds the code unclaimed goes on to register, and the
                // whole thing is one transaction so a burned code never outlives its registration.
                var claimed = await db.PairingCodes
                    .Where(c => c.Code == request.Code && c.ClaimedRegistrationId == null && c.ExpiresAt >= now)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(c => c.ClaimedRegistrationId, registration.Id),
                        cancellationToken);
                if (claimed == 0)
                {
                    return UnusableCode();
                }

                db.DaemonRegistrations.Add(registration);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return TypedResults.Ok(new ClaimResponse(registration.Id, registration.DisplayName));
            })
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ValidatesRequest<ClaimRequest>()
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }

    /// <summary>
    /// One answer for every unusable code — unknown, expired, or already claimed — so a stranger
    /// cannot sit on the endpoint and learn which codes are live.
    /// </summary>
    private static ProblemHttpResult UnusableCode()
    {
        return TypedResults.Problem(
            detail: "Unknown, expired or already-used pairing code.",
            statusCode: StatusCodes.Status404NotFound);
    }
}

internal sealed record RequestCodeRequest(
    [property: Required][property: StringLength(1000)] string PublicKeySpki,
    [property: Required][property: StringLength(200)] string Platform);

internal sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);

internal sealed record EnrollmentStatusResponse(Guid RegistrationId);

internal sealed record ClaimRequest(
    [property: Required][property: StringLength(16)] string Code,
    [property: Required][property: StringLength(200)] string DisplayName);

internal sealed record ClaimResponse(Guid RegistrationId, string DisplayName);
