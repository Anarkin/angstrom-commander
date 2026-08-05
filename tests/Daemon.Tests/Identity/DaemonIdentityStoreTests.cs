using System.Security.Cryptography;
using System.Text.Json;
using AngstromCommander.Daemon.Identity;
using AngstromCommander.Daemon.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Daemon.Tests.Identity;

public sealed class DaemonIdentityStoreTests : IDisposable
{
    private static readonly byte[] Payload = [1, 2, 3];

    private readonly string _stateDirectory;
    private readonly DaemonIdentityStore _store;

    public DaemonIdentityStoreTests()
    {
        this._stateDirectory = Directory.CreateTempSubdirectory("ac-identity-").FullName;
        this._store = new DaemonIdentityStore(
            Options.Create(new RelayOptions { StateDirectory = this._stateDirectory }),
            NullLogger<DaemonIdentityStore>.Instance);
    }

    [Fact]
    public void RegistrationIdRoundTripsAndLeavesNoTempFileBehind()
    {
        var id = Guid.NewGuid();

        this._store.SaveRegistrationId(id);

        Assert.Equal(id, this._store.LoadRegistrationId());
        // The write is temp-file-then-move; the temp must not survive it.
        Assert.Equal(
            [Path.Combine(this._stateDirectory, "registration.json")],
            Directory.GetFiles(this._stateDirectory));

        // Saving again replaces the existing file the same atomic way.
        var newer = Guid.NewGuid();
        this._store.SaveRegistrationId(newer);
        Assert.Equal(newer, this._store.LoadRegistrationId());
    }

    [Fact]
    public void CorruptRegistrationStateIsTreatedAsUnenrolled()
    {
        // A truncated write must lead back to the pairing screen, not to a Daemon that
        // crash-loops on every start.
        File.WriteAllText(Path.Combine(this._stateDirectory, "registration.json"), "{\"Registrati");

        Assert.Null(this._store.LoadRegistrationId());
    }

    [Fact]
    public void CorruptKeyIsReplacedAndTheRegistrationDropped()
    {
        File.WriteAllText(Path.Combine(this._stateDirectory, "daemon.key"), "not a PEM at all");
        this._store.SaveRegistrationId(Guid.NewGuid());

        using var key = this._store.GetOrCreateKey();

        // The fresh key is usable and persisted: a second load imports the same keypair.
        var signature = key.SignData(Payload, HashAlgorithmName.SHA256);
        using (var reloaded = this._store.GetOrCreateKey())
        {
            Assert.True(reloaded.VerifyData(Payload, signature, HashAlgorithmName.SHA256));
        }

        // The registration was bound to the lost key's public half; keeping it would leave the
        // Daemon signing challenges the Server can never accept. Dropped = pairing screen.
        Assert.Null(this._store.LoadRegistrationId());
    }

    [Fact]
    public void AHealthyKeySurvivesUnchangedAndKeepsItsRegistration()
    {
        var id = Guid.NewGuid();
        byte[] signature;
        using (var key = this._store.GetOrCreateKey())
        {
            signature = key.SignData(Payload, HashAlgorithmName.SHA256);
        }

        this._store.SaveRegistrationId(id);

        using (var reloaded = this._store.GetOrCreateKey())
        {
            Assert.True(reloaded.VerifyData(Payload, signature, HashAlgorithmName.SHA256));
        }

        Assert.Equal(id, this._store.LoadRegistrationId());
    }

    [Fact]
    public void ASavedRegistrationFileIsWellFormedJson()
    {
        this._store.SaveRegistrationId(Guid.NewGuid());

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(this._stateDirectory, "registration.json")));

        Assert.True(document.RootElement.TryGetProperty("RegistrationId", out _));
    }

    public void Dispose()
    {
        Directory.Delete(this._stateDirectory, recursive: true);
    }
}
