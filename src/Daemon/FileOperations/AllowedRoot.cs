namespace AngstromCommander.Daemon.FileOperations;

/// <summary>
/// A directory this machine shares, and whether clients may write into it. Read-only is the
/// default so sharing a folder never implies permission to modify it.
/// </summary>
internal sealed class AllowedRoot
{
    public string Path { get; set; } = string.Empty;

    public bool Writable { get; set; }
}
