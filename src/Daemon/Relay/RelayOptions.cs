namespace AngstromCommander.Daemon.Relay;

internal sealed class RelayOptions
{
    public const string SectionName = "Relay";

    public Uri? ServerUrl { get; set; }

    public string DaemonId { get; set; } = string.Empty;

    public IList<string> AllowedRoots { get; } = [];
}
