namespace AngstromCommander.Daemon.Relay;

internal sealed class RelayOptions
{
    public const string SectionName = "Relay";

    public Uri? ServerUrl { get; set; }

    /// <summary>Where the keypair and registration state live.</summary>
    public string StateDirectory { get; set; } = "state";

    public IList<string> AllowedRoots { get; } = [];
}
