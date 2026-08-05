using System.Security.Cryptography;
using System.Text.Json;
using AngstromCommander.Daemon.Relay;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Daemon.Identity;

/// <summary>
/// Persists the machine identity: an ECDsa P-256 keypair (the private key never leaves
/// this machine) and, once paired, the registration id the Server knows it by. Corrupt
/// state is treated as absent — a truncated file must lead back to the pairing screen,
/// never to a Daemon that crash-loops on every start.
/// </summary>
internal sealed partial class DaemonIdentityStore(IOptions<RelayOptions> options, ILogger<DaemonIdentityStore> logger)
{
    private const string KeyFileName = "daemon.key";
    private const string RegistrationFileName = "registration.json";

    public ECDsa GetOrCreateKey()
    {
        var path = Path.Combine(this.StateDirectory, KeyFileName);
        if (File.Exists(path))
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                key.ImportFromPem(File.ReadAllText(path));
                return key;
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                key.Dispose();
                LogCorruptKey(logger, path, ex);

                // The old private key is gone for good, and the registration was bound to its
                // public half — keeping it would leave the Daemon signing challenges the Server
                // can never accept. Dropping both leads back to the pairing screen.
                this.ClearRegistrationId();
            }
        }

        return this.CreateFreshKey(path);
    }

    public Guid? LoadRegistrationId()
    {
        var path = Path.Combine(this.StateDirectory, RegistrationFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<RegistrationState>(File.ReadAllText(path));
            return state?.RegistrationId;
        }
        catch (JsonException ex)
        {
            LogCorruptRegistration(logger, path, ex);
            return null;
        }
    }

    public void SaveRegistrationId(Guid registrationId)
    {
        Directory.CreateDirectory(this.StateDirectory);
        WriteAtomically(
            Path.Combine(this.StateDirectory, RegistrationFileName),
            JsonSerializer.Serialize(new RegistrationState(registrationId)));
    }

    // The keypair deliberately survives: it is this machine's identity, and re-enrolling
    // binds the same public key to a fresh registration.
    public void ClearRegistrationId()
    {
        File.Delete(Path.Combine(this.StateDirectory, RegistrationFileName));
    }

    private ECDsa CreateFreshKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            Directory.CreateDirectory(this.StateDirectory);
            WriteAtomically(path, key.ExportECPrivateKeyPem());
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    // Write-then-rename, so a crash mid-write leaves either the previous file or none —
    // never a truncated one for the next start to choke on.
    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    private string StateDirectory => options.Value.StateDirectory;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored key at {Path} is unreadable; generating a fresh identity and re-enrolling.")]
    private static partial void LogCorruptKey(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored registration at {Path} is unreadable; treating this machine as unenrolled.")]
    private static partial void LogCorruptRegistration(ILogger logger, string path, Exception exception);

    private sealed record RegistrationState(Guid RegistrationId);
}
