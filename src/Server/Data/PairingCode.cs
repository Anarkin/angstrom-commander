namespace AngstromCommander.Server.Data;

/// <summary>Short-lived one-time code shown by an enrolling Daemon and entered by a logged-in user.</summary>
internal sealed class PairingCode
{
    public string Code { get; set; } = string.Empty;

    public string PublicKeySpki { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when a user claims the code; the enrolling Daemon polls for this.</summary>
    public Guid? ClaimedRegistrationId { get; set; }
}
