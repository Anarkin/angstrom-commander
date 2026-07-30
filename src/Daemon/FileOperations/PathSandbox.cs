namespace AngstromCommander.Daemon.FileOperations;

/// <summary>
/// Confines every client-supplied path to the configured allowed roots: paths are
/// canonicalized before checking, so traversal segments and relative tricks cannot
/// escape. An empty root list means no path is accessible, and writes additionally
/// require the containing root to be marked writable.
/// </summary>
internal sealed class PathSandbox
{
    // Windows paths are case-insensitive; the comparison must match the file system.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IReadOnlyList<AllowedRoot> _allowedRoots;

    public PathSandbox(IEnumerable<AllowedRoot> allowedRoots)
    {
        this._allowedRoots = allowedRoots
            .Select(static root => new AllowedRoot
            {
                Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)),
                Writable = root.Writable,
            })
            .ToList();
    }

    public bool TryResolveForRead(string requestedPath, out string resolvedPath)
    {
        return this.TryResolve(requestedPath, requireWritable: false, out resolvedPath);
    }

    public bool TryResolveForWrite(string requestedPath, out string resolvedPath)
    {
        return this.TryResolve(requestedPath, requireWritable: true, out resolvedPath);
    }

    private bool TryResolve(string requestedPath, bool requireWritable, out string resolvedPath)
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
            if (requireWritable && !root.Writable)
            {
                continue;
            }

            if (Contains(root.Path, canonical))
            {
                resolvedPath = canonical;
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string root, string canonical)
    {
        if (canonical.Equals(root, PathComparison))
        {
            return true;
        }

        return canonical.StartsWith(root, PathComparison)
            && canonical.Length > root.Length
            && (canonical[root.Length] == Path.DirectorySeparatorChar
                || canonical[root.Length] == Path.AltDirectorySeparatorChar);
    }
}
