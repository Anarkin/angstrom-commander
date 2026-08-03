namespace AngstromCommander.Server.Auth;

internal static class AuthClaims
{
    public const string Subject = "sub";
    public const string TokenType = "token_type";
    public const string UserTokenType = "user";
    public const string DaemonTokenType = "daemon";
    public const string PatTokenType = "pat";
    public const string DaemonRegistrationId = "daemon_registration_id";
    public const string SessionId = "session_id";
    public const string Scopes = "scope";
}

internal static class AuthPolicies
{
    /// <summary>A WebClient/MobileClient user session token.</summary>
    public const string User = "User";

    /// <summary>A short-lived connection token minted for a Daemon that proved key ownership.</summary>
    public const string Daemon = "Daemon";

    /// <summary>A personal access token — the MCP endpoint's audience, and only that.</summary>
    public const string Mcp = "Mcp";
}
