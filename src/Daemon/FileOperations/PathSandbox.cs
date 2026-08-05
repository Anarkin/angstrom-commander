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

    // Linux caps one resolution at 40 link follows (ELOOP); the same budget here bounds any
    // chain — or cycle — a request can send the resolver through, and running out refuses.
    private const int MaxLinkFollows = 40;

    private readonly IReadOnlyList<AllowedRoot> _allowedRoots;
    private readonly string? _neverShared;

    /// <param name="allowedRoots">The directories this machine shares.</param>
    /// <param name="neverShared">
    /// A directory that stays unreachable however the roots are configured — the Daemon's own
    /// state, which holds the private key that IS this machine's identity. Sharing a folder that
    /// happens to contain it (a home directory, or the whole disk) would otherwise hand out the
    /// means to impersonate the machine, and the sandbox is the only thing in a position to know.
    /// </param>
    public PathSandbox(IEnumerable<AllowedRoot> allowedRoots, string? neverShared = null)
    {
        this._neverShared = neverShared is null || !TryMakeReal(neverShared, out var resolved) ? null : resolved;

        // Roots go through the same link resolution as requests, so both sides are real
        // locations and a root that is itself reached through a link still matches.
        this._allowedRoots = allowedRoots
            .Select(static root => new AllowedRoot
            {
                // A root we cannot resolve is a configuration error: fail startup loudly rather
                // than share a path the sandbox cannot vouch for. A root that merely does not
                // exist yet still resolves — a missing component is not a link.
                Path = TryMakeReal(root.Path, out var real)
                    ? real
                    : throw new InvalidOperationException(
                        $"Allowed root '{root.Path}' cannot be resolved and will not be shared."),
                Writable = root.Writable,
            })
            .ToList();
    }

    /// <summary>
    /// The resolved roots, for telling the machine's owner what is shared — the resolved
    /// (not configured) form, because that is what listings and paths speak.
    /// </summary>
    public IReadOnlyList<AllowedRoot> Roots => this._allowedRoots;

    public bool TryResolveForRead(string requestedPath, out string resolvedPath)
    {
        return this.TryResolve(requestedPath, requireWritable: false, out resolvedPath);
    }

    public bool TryResolveForWrite(string requestedPath, out string resolvedPath)
    {
        return this.TryResolve(requestedPath, requireWritable: true, out resolvedPath);
    }

    /// <summary>
    /// Resolves a path about to be created, renamed, or deleted. The parent directory gets
    /// the full write resolution (links followed, writable root required), but the final
    /// component is taken as it is — deleting or moving a link must act on the link itself,
    /// never on whatever it points at, and full resolution would swap one for the other.
    /// The shared roots themselves are refused: sharing a folder is not permission to
    /// delete or rename the folder.
    /// </summary>
    public bool TryResolveForMutation(string requestedPath, out string resolvedPath)
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

        var parent = Path.GetDirectoryName(canonical);
        var name = Path.GetFileName(canonical);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
        {
            // A filesystem root has no parent to resolve and is nobody's to mutate.
            return false;
        }

        if (!this.TryResolveForWrite(parent, out var resolvedParent))
        {
            return false;
        }

        var candidate = Path.Combine(resolvedParent, name);
        foreach (var root in this._allowedRoots)
        {
            if (candidate.Equals(root.Path, PathComparison))
            {
                return false;
            }
        }

        if (this._neverShared is not null && Contains(this._neverShared, candidate))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    private bool TryResolve(string requestedPath, bool requireWritable, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (!TryMakeReal(requestedPath, out var real))
        {
            return false;
        }

        if (this._neverShared is not null && Contains(this._neverShared, real))
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

        var remaining = new Stack<string>();
        if (!TrySpliceCanonical(path, basePath: null, remaining, out var resolved))
        {
            return false;
        }

        // Segment by segment from the root down: a link anywhere along the path moves the real
        // location, not just a link at the end. A followed link's target is spliced back into
        // the walk and re-walked the same way, one hop at a time — the target string can itself
        // run through further links, so trusting it as-is would let one link launder another's
        // escape. The hop budget turns a chain too long — or circular — into a refusal.
        var followsLeft = MaxLinkFollows;
        while (remaining.Count > 0)
        {
            var candidate = Path.Combine(resolved, remaining.Pop());
            if (!TryGetLinkTarget(candidate, out var linkTarget))
            {
                return false;
            }

            if (linkTarget is null)
            {
                resolved = candidate;
                continue;
            }

            if (--followsLeft < 0 || !TrySpliceCanonical(linkTarget, basePath: resolved, remaining, out resolved))
            {
                return false;
            }
        }

        real = Path.TrimEndingDirectorySeparator(resolved);
        return true;
    }

    /// <summary>
    /// Canonicalizes a path (a relative one against <paramref name="basePath"/> — a link target
    /// is relative to the directory holding the link) and pushes its segments onto the walk in
    /// order, leaving <paramref name="resolved"/> back at the file-system root.
    /// </summary>
    private static bool TrySpliceCanonical(string path, string? basePath, Stack<string> remaining, out string resolved)
    {
        resolved = string.Empty;

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(
                basePath is null ? Path.GetFullPath(path) : Path.GetFullPath(path, basePath));
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

        var segments = canonical[pathRoot.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            remaining.Push(segments[i]);
        }

        resolved = pathRoot;
        return true;
    }

    /// <summary>
    /// Reads a single link hop. A null target means the component is not a link — including one
    /// that does not exist yet, which is the normal case for the file an upload is about to
    /// create (its parents were already resolved by the time the walk reaches it). A dangling
    /// link still yields its target, because it still decides where a write through it lands.
    /// </summary>
    private static bool TryGetLinkTarget(string path, out string? linkTarget)
    {
        linkTarget = null;
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            linkTarget = entry.LinkTarget;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A link we are not allowed to read: refuse rather than guess.
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
