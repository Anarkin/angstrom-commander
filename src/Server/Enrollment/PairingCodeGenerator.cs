using System.Security.Cryptography;

namespace AngstromCommander.Server.Enrollment;

internal static class PairingCodeGenerator
{
    // No 0/O/1/I: the user retypes this code by hand.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 8;

    public static string Generate()
    {
        return new string(RandomNumberGenerator.GetItems<char>(Alphabet, Length));
    }
}
