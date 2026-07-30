using System.Text;
using AngstromCommander.Server.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AngstromCommander.Server.Auth;

internal sealed class TokenService(IOptions<AuthOptions> options)
{
    private static readonly TimeSpan UserTokenLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan DaemonTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Turns the configured secret into a signing key, refusing one too short to sign with. Shared
    /// with the bearer-validation setup so issuing and validating can never disagree about the key,
    /// and so a Server configured without one fails closed with a message naming the setting rather
    /// than an opaque argument error from deep inside the token library.
    /// </summary>
    /// <remarks>
    /// Checked here rather than at host start because build-time OpenAPI generation starts the app
    /// with no environment configuration; this runs the first time a token is issued or validated.
    /// </remarks>
    public static SymmetricSecurityKey CreateSigningKey(string configuredKey)
    {
        if (Encoding.UTF8.GetByteCount(configuredKey) < AuthOptions.MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"Auth:JwtSigningKey must be configured with at least {AuthOptions.MinimumSigningKeyBytes} bytes. "
                + "Development reads one from appsettings.Development.json; every other environment must set "
                + "Auth__JwtSigningKey to its own secret.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuredKey));
    }

    public string CreateUserToken(AppUser user)
    {
        return this.CreateToken(
            new Dictionary<string, object>
            {
                [AuthClaims.Subject] = user.Id.ToString(),
                [AuthClaims.TokenType] = AuthClaims.UserTokenType,
            },
            UserTokenLifetime);
    }

    public string CreateDaemonToken(Guid registrationId)
    {
        return this.CreateToken(
            new Dictionary<string, object>
            {
                [AuthClaims.DaemonRegistrationId] = registrationId.ToString(),
                [AuthClaims.TokenType] = AuthClaims.DaemonTokenType,
            },
            DaemonTokenLifetime);
    }

    private string CreateToken(Dictionary<string, object> claims, TimeSpan lifetime)
    {
        var auth = options.Value;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer,
            Audience = auth.Audience,
            Expires = DateTime.UtcNow.Add(lifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                CreateSigningKey(auth.JwtSigningKey), SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
