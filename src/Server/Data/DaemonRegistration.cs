namespace AngstromCommander.Server.Data;

/// <summary>
/// A paired machine. The public key IS the Daemon's identity: connecting requires
/// signing a challenge with the matching private key, which never leaves the machine.
/// DisplayName is a per-user label and never routes anything.
/// </summary>
internal sealed class DaemonRegistration
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Base64 SubjectPublicKeyInfo (ECDsa P-256).</summary>
    public string PublicKeySpki { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
