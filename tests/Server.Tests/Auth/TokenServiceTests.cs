using AngstromCommander.Server.Auth;

namespace AngstromCommander.Server.Tests.Auth;

public class TokenServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("thirty-one-bytes-is-still-short")]
    public void RefusesASigningKeyTooShortToSignWith(string configuredKey)
    {
        // No key at all is the case that matters: nothing ships a default, so an environment
        // that forgot to configure one must fail closed rather than sign with something weak.
        var failure = Assert.Throws<InvalidOperationException>(() => TokenService.CreateSigningKey(configuredKey));

        Assert.Contains("Auth:JwtSigningKey", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsAKeyOfTheRequiredLength()
    {
        var key = TokenService.CreateSigningKey(new string('k', AuthOptions.MinimumSigningKeyBytes));

        Assert.Equal(AuthOptions.MinimumSigningKeyBytes * 8, key.KeySize);
    }
}
