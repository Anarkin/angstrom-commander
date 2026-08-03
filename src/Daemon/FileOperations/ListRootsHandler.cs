using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

/// <summary>
/// Tells the machine's owner what this Daemon shares — the discoverability half of the
/// sandbox: without it a user faces an empty path box and a refusal, with no way to know
/// what would be accepted.
/// </summary>
internal sealed class ListRootsHandler(PathSandbox sandbox)
{
    public ListRootsResponse Handle()
    {
        return new ListRootsResponse(
            sandbox.Roots.Select(static root => new SharedRoot(root.Path, root.Writable)).ToList());
    }
}
