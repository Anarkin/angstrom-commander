using System.ComponentModel.DataAnnotations;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Auth;

// Managed with a user token only: a PAT must never mint or revoke PATs, or one leaked
// token quietly becomes all of them.
internal static class PatEndpoints
{
    public static IEndpointRouteBuilder MapPatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/pats",
            static async Task<Ok<PatCreatedResponse>> (
                CreatePatRequest request,
                HttpContext http,
                AppDbContext db,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                // The only moment the token value exists in the clear; from here on,
                // hash-only, like Daemon public keys.
                var token = PersonalAccessTokens.Generate();
                var session = new UserSession
                {
                    Id = Guid.NewGuid(),
                    UserId = GetUserId(http),
                    Kind = UserSession.PatKind,
                    Name = request.Name,
                    TokenHash = PersonalAccessTokens.Hash(token),
                    Scopes = PersonalAccessTokens.FilesScope,
                    CreatedAt = clock.GetUtcNow(),
                };
                db.Sessions.Add(session);
                await db.SaveChangesAsync(cancellationToken);

                return TypedResults.Ok(
                    new PatCreatedResponse(session.Id, session.Name, token, session.CreatedAt));
            })
            .ValidatesRequest<CreatePatRequest>()
            .RequireAuthorization(AuthPolicies.User);

        app.MapGet(
            "/api/pats",
            static async Task<Ok<IReadOnlyList<PatResponse>>> (
                HttpContext http,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                IReadOnlyList<PatResponse> tokens = await db.Sessions.AsNoTracking()
                    .Where(s => s.UserId == userId && s.Kind == UserSession.PatKind && s.RevokedAt == null)
                    .OrderBy(static s => s.CreatedAt)
                    .Select(static s => new PatResponse(s.Id, s.Name, s.CreatedAt, s.LastUsedAt))
                    .ToListAsync(cancellationToken);
                return TypedResults.Ok(tokens);
            })
            .RequireAuthorization(AuthPolicies.User);

        app.MapDelete(
            "/api/pats/{id:guid}",
            static async Task<Results<NoContent, ProblemHttpResult>> (
                Guid id,
                HttpContext http,
                AppDbContext db,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var userId = GetUserId(http);
                var session = await db.Sessions.FirstOrDefaultAsync(
                    s => s.Id == id && s.UserId == userId && s.Kind == UserSession.PatKind && s.RevokedAt == null,
                    cancellationToken);
                if (session is null)
                {
                    return TypedResults.Problem(
                        detail: "No such token.", statusCode: StatusCodes.Status404NotFound);
                }

                session.RevokedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                return TypedResults.NoContent();
            })
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthPolicies.User);

        return app;
    }

    private static Guid GetUserId(HttpContext http)
    {
        return Guid.Parse(http.User.FindFirst(AuthClaims.Subject)!.Value);
    }
}

/// <summary>Names the token so its owner can tell which tool holds it.</summary>
internal sealed record CreatePatRequest(
    [property: Required][property: StringLength(200, MinimumLength = 1)] string Name);

/// <summary>The one and only response carrying the token value itself.</summary>
internal sealed record PatCreatedResponse(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);

/// <summary>A live token, value withheld — only its hash exists anymore.</summary>
internal sealed record PatResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
