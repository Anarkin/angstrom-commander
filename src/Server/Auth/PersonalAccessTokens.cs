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

    public static string Generate()
    {
        return Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    public static string Hash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
