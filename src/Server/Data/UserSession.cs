namespace AngstromCommander.Server.Data;

/// <summary>
/// A stored, revocable credential — the row behind the future "active sessions" page.
/// Personal access tokens live here today; refresh tokens and OAuth grants join later
/// with their own kinds. Only the token's SHA-256 hash is stored: like Daemon public
/// keys, a leaked database must not hand out the credentials themselves.
/// </summary>
internal sealed class UserSession
{
    public const string PatKind = "pat";

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>"pat" today; "refresh" and "oauth" when those arrive.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>User-chosen label, e.g. "Claude Code on my laptop". Never routes anything.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex SHA-256 of the full token value.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Space-separated scopes; "files" is the whole file-op surface today.</summary>
    public string Scopes { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
