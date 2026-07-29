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
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtSigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
