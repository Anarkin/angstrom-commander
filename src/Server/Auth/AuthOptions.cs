namespace AngstromCommander.Server.Auth;

internal sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>HMAC-SHA256's key size — a shorter key weakens every token the Server issues.</summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// Symmetric JWT signing key. No default ships with the repo — a known key would let anyone
    /// mint a token for any account — so each environment supplies its own (Development reads one
    /// from appsettings.Development.json, others set <c>Auth__JwtSigningKey</c>) and the Server
    /// refuses to start without it.
    /// </summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "angstrom-commander";

    public string Audience { get; set; } = "angstrom-commander";
}
