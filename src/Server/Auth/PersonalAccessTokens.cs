using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace AngstromCommander.Server.Auth;

/// <summary>
/// The PAT value format and its hashing. The prefix makes tokens recognizable — to the
/// auth scheme router, to secret scanners, and to whoever finds one in a config file.
/// Only the SHA-256 hash is ever stored.
/// </summary>
internal static class PersonalAccessTokens
{
    public const string Prefix = "acpat_";

    /// <summary>The whole file-op surface; finer scopes can join without a schema change.</summary>
    public const string FilesScope = "files";

    /// <summary>
    /// The PAT carried in an Authorization header, or null if the header holds something else.
    /// One reading of the header for the scheme router and the handler alike, so they can never
    /// disagree about what counts as a PAT.
    /// </summary>
    /// <remarks>
    /// The scheme is matched case-insensitively because RFC 7235 says it is case-insensitive
    /// and <c>JwtBearerHandler</c> treats it that way — a client sending "bearer acpat_…" would
    /// otherwise be routed to the JWT handler and told nothing useful. The value is trimmed for
    /// the same reason the framework trims it: a stray trailing space would silently hash to
    /// something that matches no stored token.
    /// </remarks>
    public static string? ReadFromHeader(string? authorizationHeader)
    {
        const string scheme = "Bearer ";
        if (authorizationHeader is null
            || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = authorizationHeader[scheme.Length..].Trim();
        return value.StartsWith(Prefix, StringComparison.Ordinal) ? value : null;
    }

    public static string Generate()
    {
        return Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    public static string Hash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
