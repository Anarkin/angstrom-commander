using System.Security.Cryptography;
using AngstromCommander.Server.Enrollment;

namespace AngstromCommander.Server.Tests.Enrollment;

public class PublicKeysTests
{
    [Fact]
    public void ImportsValidSpkiAndVerifiesSignature()
    {
        using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(original.ExportSubjectPublicKeyInfo());
        var payload = new byte[] { 1, 2, 3 };
        var signature = original.SignData(payload, HashAlgorithmName.SHA256);

        Assert.True(PublicKeys.TryImportSpki(spki, out var imported));
        using (imported)
        {
            Assert.True(imported.VerifyData(payload, signature, HashAlgorithmName.SHA256));
        }
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.False(PublicKeys.TryImportSpki("not-base64!!", out _));
        Assert.False(PublicKeys.TryImportSpki(Convert.ToBase64String([1, 2, 3]), out _));
    }
}
