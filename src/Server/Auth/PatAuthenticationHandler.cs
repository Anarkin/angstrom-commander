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
        var header = this.Request.Headers.Authorization.ToString();
        if (!header.StartsWith($"Bearer {PersonalAccessTokens.Prefix}", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = PersonalAccessTokens.Hash(header["Bearer ".Length..]);
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

        var identity = new ClaimsIdentity(
        [
            new Claim(AuthClaims.Subject, session.UserId.ToString()),
            new Claim(AuthClaims.TokenType, AuthClaims.PatTokenType),
            new Claim(AuthClaims.SessionId, session.Id.ToString()),
            new Claim(AuthClaims.Scopes, session.Scopes),
        ],
        SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
