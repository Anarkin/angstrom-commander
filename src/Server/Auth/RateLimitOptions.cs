namespace AngstromCommander.Server.Auth;

/// <summary>
/// How many requests an anonymous caller gets per minute. Configurable so an environment can
/// tighten or loosen it without a deploy — and so tests can drive it to either extreme.
/// </summary>
internal sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Sign-in, registration, and the Daemon's challenge/token exchange. Account lockout is what
    /// actually stops password guessing; this stops one caller from drowning the endpoint.
    /// </summary>
    public int AuthenticationPermitsPerMinute { get; set; } = 20;

    /// <summary>
    /// Enrollment status, which an unpaired Daemon polls every few seconds — several machines
    /// behind one home address share the allowance, so it has to be roomier.
    /// </summary>
    public int EnrollmentPollPermitsPerMinute { get; set; } = 120;
}

internal static class RateLimitPolicies
{
    public const string Authentication = "authentication";

    public const string EnrollmentPolling = "enrollment-polling";
}
