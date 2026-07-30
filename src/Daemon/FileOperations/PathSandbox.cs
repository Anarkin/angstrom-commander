namespace AngstromCommander.Daemon.FileOperations;

/// <summary>
/// Confines every client-supplied path to the configured allowed roots: paths are
/// canonicalized and their symbolic links resolved before checking, so neither traversal
/// segments nor a link planted inside a shared folder can escape. An empty root list means
/// no path is accessible, and writes additionally require the containing root to be marked
/// writable.
/// </summary>
internal sealed class PathSandbox
{
    // Windows paths are case-insensitive; the comparison must match the file system.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly IReadOnlyList<AllowedRoot> _allowedRoots;

    public PathSandbox(IEnumerable<AllowedRoot> allowedRoots)
    {
        // Roots go through the same link resolution as requests, so both sides are real
        // locations and a root that is itself reached through a link still matches.
        this._allowedRoots = allowedRoots
            .Select(static root => new AllowedRoot
            {
                // A root we cannot resolve is a configuration error: keep its canonical form so
                // startup still fails loudly on a malformed path rather than silently sharing it.
                Path = TryMakeReal(root.Path, out var real) ? real : Path.GetFullPath(root.Path),
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

        if (!TryMakeReal(requestedPath, out var real))
        {
            return false;
        }

        foreach (var root in this._allowedRoots)
        {
            if (requireWritable && !root.Writable)
            {
                continue;
            }

            if (Contains(root.Path, real))
            {
                resolvedPath = real;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The real location a path refers to: canonicalized, then with every symbolic link and
    /// junction along it followed. Canonicalizing alone is purely lexical — it collapses "..",
    /// but a link inside an allowed root still points wherever it likes, and the file system
    /// follows it. Returns false for a path that cannot be resolved at all, so anything we
    /// cannot vouch for is refused rather than assumed safe.
    /// </summary>
    private static bool TryMakeReal(string path, out string real)
    {
        real = string.Empty;

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var pathRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return false;
        }

        // Segment by segment from the root down: a link anywhere along the path moves the real
        // location, not just a link at the end.
        var resolved = pathRoot;
        foreach (var segment in canonical[pathRoot.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            if (!TryFollowLink(resolved, out var target))
            {
                return false;
            }

            resolved = target;
        }

        real = Path.TrimEndingDirectorySeparator(resolved);
        return true;
    }

    /// <summary>
    /// Follows one path component if it is a link, to the end of any chain of links. A component
    /// that does not exist is not a link and stays as it is — an upload names a file that is not
    /// there yet, and its parents have already been resolved by the time we reach it.
    /// </summary>
    private static bool TryFollowLink(string path, out string target)
    {
        target = path;
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (entry.LinkTarget is null)
            {
                // Not a link — including a component that does not exist yet, which is the normal
                // case for the file an upload is about to create.
                return true;
            }

            // A dangling link still decides where a write through it would land, so when the chain
            // cannot be walked to the end, fall back to the one hop we can read.
            var resolved = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(entry.LinkTarget, Path.GetDirectoryName(path) ?? string.Empty);

            target = Path.TrimEndingDirectorySeparator(resolved);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A circular chain, or one we are not allowed to read: refuse rather than guess.
            return false;
        }
    }

    private static bool Contains(string root, string canonical)
    {
        if (canonical.Equals(root, PathComparison))
        {
            return true;
        }

        // The prefix has to carry a separator so "/data" never matches "/database". A file-system
        // root ("/", "C:\") already ends in one and must not get a second, or sharing the whole
        // machine would match nothing but itself.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return canonical.StartsWith(prefix, PathComparison);
    }
}
