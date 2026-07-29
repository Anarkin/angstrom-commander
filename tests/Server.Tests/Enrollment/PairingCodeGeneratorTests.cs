using AngstromCommander.Server.Enrollment;

namespace AngstromCommander.Server.Tests.Enrollment;

public class PairingCodeGeneratorTests
{
    [Fact]
    public void GeneratesEightCharsFromUnambiguousAlphabet()
    {
        var code = PairingCodeGenerator.Generate();

        Assert.Equal(8, code.Length);
        Assert.All(code, static c => Assert.Contains(c, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
    }

    [Fact]
    public void GeneratesDistinctCodes()
    {
        var codes = Enumerable.Range(0, 100).Select(static _ => PairingCodeGenerator.Generate()).ToHashSet();

        Assert.True(codes.Count > 90);
    }
}
