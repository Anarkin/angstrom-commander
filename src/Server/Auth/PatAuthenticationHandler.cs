using System.Security.Claims;
using System.Text.Encodings.Web;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Server.Auth;

/// <summary>
/// Authenticates personal access tokens: hash the presented value, find the live
/// session row, and the caller is that user — through a "pat" principal, which the
/// endpoint policies deliberately keep off everything except the MCP surface.
/// </summary>
internal sealed class PatAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PersonalAccessToken";

    // LastUsedAt is a human-facing "is this token still in use" signal, not an audit
    // log; a coarse grain keeps it from costing a write per request.
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = PersonalAccessTokens.ReadFromHeader(this.Request.Headers.Authorization);
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = PersonalAccessTokens.Hash(token);
        var db = this.Context.RequestServices.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.FirstOrDefaultAsync(
            s => s.TokenHash == hash && s.Kind == UserSession.PatKind && s.RevokedAt == null);
        if (session is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked token.");
        }

        var now = this.Context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (session.LastUsedAt is null || now - session.LastUsedAt > LastUsedGranularity)
        {
            session.LastUsedAt = now;
            await db.SaveChangesAsync();
        }

        List<Claim> claims =
        [
            new(AuthClaims.Subject, session.UserId.ToString()),
            new(AuthClaims.TokenType, AuthClaims.PatTokenType),
            new(AuthClaims.SessionId, session.Id.ToString()),
        ];

        // One claim per scope. The column holds them space-separated, and an authorization
        // policy's RequireClaim matches a claim's whole value — so the day a second scope
        // exists, a single claim reading "files read" would stop equalling "files" and the
        // token would be refused everywhere, with a bare 401 and nothing to debug against.
        claims.AddRange(session.Scopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static scope => new Claim(AuthClaims.Scopes, scope)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
