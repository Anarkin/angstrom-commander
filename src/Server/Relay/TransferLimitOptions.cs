namespace AngstromCommander.Server.Relay;

/// <summary>
/// Ceilings on what one user may move through the relay. Every transferred byte leaves
/// Azure exactly once — to a browser or to a receiving Daemon — so the daily quota is,
/// byte for byte, a bound on what one account can cost in egress, and the bandwidth cap
/// bounds how fast anyone can get there. Zero disables a limit; configurable per
/// environment like the rate limits.
/// </summary>
internal sealed class TransferLimitOptions
{
    public const string SectionName = "TransferLimits";

    /// <summary>Relayed bytes one user gets per UTC day. Default 10 GiB.</summary>
    public long DailyBytesPerUser { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Sustained relay throughput one user gets across all their transfers at once.
    /// Default 12.5 MB/s (100 Mbit/s) — invisible in normal use, decisive against abuse.
    /// </summary>
    public long BytesPerSecondPerUser { get; set; } = 12_500_000;
}

/// <summary>
/// Thrown mid-transfer when a chunk crosses the owner's daily quota; the transfer is
/// already abandoned by then. Distinct so endpoints can answer 429 rather than a
/// machine-fault status.
/// </summary>
internal sealed class TransferQuotaExceededException : IOException
{
    public const string UserFacingMessage = "Daily transfer limit reached. It resets at midnight UTC.";

    public TransferQuotaExceededException()
        : base(UserFacingMessage)
    {
    }

    // The analyzer's standard constructor set; nothing in the codebase varies the message.
    public TransferQuotaExceededException(string message)
        : base(message)
    {
    }

    public TransferQuotaExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
