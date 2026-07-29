namespace AngstromCommander.Server.Auth;

internal sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Symmetric JWT signing key; must be at least 32 bytes. Dev value in appsettings, real value via environment.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "angstrom-commander";

    public string Audience { get; set; } = "angstrom-commander";
}
