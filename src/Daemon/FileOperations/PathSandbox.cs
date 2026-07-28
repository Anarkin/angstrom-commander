namespace AngstromCommander.Daemon.FileOperations;

/// <summary>
/// Confines every client-supplied path to the configured allowed roots: paths are
/// canonicalized before checking, so traversal segments and relative tricks cannot
/// escape. An empty root list means no path is accessible.
/// </summary>
internal sealed class PathSandbox
{
    // Windows paths are case-insensitive; the comparison must match the file system.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IReadOnlyList<string> _allowedRoots;

    public PathSandbox(IEnumerable<string> allowedRoots)
    {
        this._allowedRoots = allowedRoots
            .Select(static root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .ToList();
    }

    public bool TryResolve(string requestedPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        foreach (var root in this._allowedRoots)
        {
            if (canonical.Equals(root, PathComparison)
                || (canonical.StartsWith(root, PathComparison)
                    && canonical.Length > root.Length
                    && (canonical[root.Length] == Path.DirectorySeparatorChar
                        || canonical[root.Length] == Path.AltDirectorySeparatorChar)))
            {
                resolvedPath = canonical;
                return true;
            }
        }

        return false;
    }
}
