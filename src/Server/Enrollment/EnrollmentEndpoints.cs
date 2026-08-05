using System.ComponentModel.DataAnnotations;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Enrollment;

internal static class EnrollmentEndpoints
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a claimed code stays answerable, counted from the claim. Long enough for the
    /// enrolling Daemon's next poll to collect its registration id, short enough that the code
    /// stops being a lookup for that id almost immediately afterwards.
    /// </summary>
    private static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(2);

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
                // Expired codes are swept opportunistically at the same time — claimed ones
                // included, or the table would keep one row per pairing forever and every one
                // of them would stay an anonymous code-to-registrationId lookup.
                var now = DateTimeOffset.UtcNow;
                await db.PairingCodes
                    .Where(c => (c.ClaimedRegistrationId == null && c.PublicKeySpki == request.PublicKeySpki)
                        || c.ExpiresAt < now)
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
                // Expiry applies whether or not the code was claimed. Exempting claimed ones
                // (so the Daemon could still collect its registration id) left every code that
                // ever paired answering forever: a code from a screenshot or a scrollback stayed
                // an anonymous lookup from a short string to a live registration id, which is
                // enough to drive the challenge endpoint and confirm the machine is still
                // enrolled. Claiming extends the window instead — see ClaimGrace below.
                var pairingCode = await db.PairingCodes.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
                if (pairingCode is null || pairingCode.ExpiresAt < DateTimeOffset.UtcNow)
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
            static async Task<Results<Ok<ClaimResponse>, ProblemHttpResult>> (
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
                    return TypedResults.Problem(
                        detail: "This session is no longer valid. Sign in again.",
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var now = DateTimeOffset.UtcNow;

                // Looked up before the transaction opens: a wrong code is the common case
                // (people mistype them), and it has no business starting one.
                var pairingCode = await db.PairingCodes.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Code == request.Code, cancellationToken);
                if (pairingCode is null
                    || pairingCode.ClaimedRegistrationId is not null
                    || pairingCode.ExpiresAt < now)
                {
                    return UnusableCode();
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

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
                // Claiming also pushes the expiry out, so a code claimed in its last seconds
                // still survives long enough for the Daemon's next poll to collect the
                // registration id. Without it the machine would sit on the pairing screen
                // holding a code that had already paired to the account.
                var graceUntil = now.Add(ClaimGrace);
                var claimed = await db.PairingCodes
                    .Where(c => c.Code == request.Code && c.ClaimedRegistrationId == null && c.ExpiresAt >= now)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(c => c.ClaimedRegistrationId, registration.Id)
                            .SetProperty(c => c.ExpiresAt, c => c.ExpiresAt > graceUntil ? c.ExpiresAt : graceUntil),
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
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ValidatesRequest<ClaimRequest>()
            // Guessing codes is impractical on entropy alone, but every other endpoint that
            // takes a guessable secret is throttled and this one was not: unthrottled, a single
            // account can put an unbounded stream of misses through it.
            .RequireRateLimiting(RateLimitPolicies.Authentication)
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
