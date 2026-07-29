using System.Security.Cryptography;
using System.Text.Json;
using AngstromCommander.Daemon.Relay;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Daemon.Identity;

/// <summary>
/// Persists the machine identity: an ECDsa P-256 keypair (the private key never leaves
/// this machine) and, once paired, the registration id the Server knows it by.
/// </summary>
internal sealed class DaemonIdentityStore(IOptions<RelayOptions> options)
{
    private const string KeyFileName = "daemon.key";
    private const string RegistrationFileName = "registration.json";

    public ECDsa GetOrCreateKey()
    {
        var path = Path.Combine(this.StateDirectory, KeyFileName);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            Directory.CreateDirectory(this.StateDirectory);
            File.WriteAllText(path, key.ExportECPrivateKeyPem());
        }

        return key;
    }

    public Guid? LoadRegistrationId()
    {
        var path = Path.Combine(this.StateDirectory, RegistrationFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<RegistrationState>(File.ReadAllText(path));
        return state?.RegistrationId;
    }

    public void SaveRegistrationId(Guid registrationId)
    {
        Directory.CreateDirectory(this.StateDirectory);
        File.WriteAllText(
            Path.Combine(this.StateDirectory, RegistrationFileName),
            JsonSerializer.Serialize(new RegistrationState(registrationId)));
    }

    private string StateDirectory => options.Value.StateDirectory;

    private sealed record RegistrationState(Guid RegistrationId);
}
