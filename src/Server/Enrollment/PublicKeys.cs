using System.Security.Cryptography;

namespace AngstromCommander.Server.Enrollment;

internal static class PublicKeys
{
    /// <summary>Validates a base64 SubjectPublicKeyInfo and hands the caller the imported key.</summary>
    public static bool TryImportSpki(string publicKeySpki, out ECDsa key)
    {
        key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            key.Dispose();
            key = null!;
            return false;
        }
    }
}
