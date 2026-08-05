using System.Diagnostics;
using AngstromCommander.Daemon.FileOperations;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public class PathSandboxTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sandbox-root");

    private static PathSandbox ReadOnlySandbox()
    {
        return new PathSandbox([new AllowedRoot { Path = Root, Writable = false }]);
    }

    private static PathSandbox WritableSandbox()
    {
        return new PathSandbox([new AllowedRoot { Path = Root, Writable = true }]);
    }

    [Fact]
    public void AllowsPathInsideRoot()
    {
        var allowed = ReadOnlySandbox().TryResolveForRead(Path.Combine(Root, "sub", "file.txt"), out var resolved);

        Assert.True(allowed);
        Assert.Equal(Path.Combine(Root, "sub", "file.txt"), resolved);
    }

    [Fact]
    public void AllowsRootItself()
    {
        Assert.True(ReadOnlySandbox().TryResolveForRead(Root, out _));
    }

    [Fact]
    public void BlocksPathOutsideRoots()
    {
        Assert.False(ReadOnlySandbox().TryResolveForRead(Path.Combine(Path.GetTempPath(), "elsewhere"), out _));
    }

    [Fact]
    public void BlocksTraversalEscapingRoot()
    {
        Assert.False(ReadOnlySandbox().TryResolveForRead(Path.Combine(Root, "..", "evil"), out _));
    }

    [Fact]
    public void ResolvesTraversalStayingInsideRoot()
    {
        var allowed = ReadOnlySandbox().TryResolveForRead(Path.Combine(Root, "sub", "..", "file.txt"), out var resolved);

        Assert.True(allowed);
        Assert.Equal(Path.Combine(Root, "file.txt"), resolved);
    }

    [Fact]
    public void BlocksSiblingSharingRootPrefix()
    {
        Assert.False(ReadOnlySandbox().TryResolveForRead(Root + "2", out _));
    }

    [Fact]
    public void BlocksEverythingWhenNoRootsConfigured()
    {
        Assert.False(new PathSandbox([]).TryResolveForRead(Root, out _));
    }

    [Fact]
    public void RefusesToStartWithARootItCannotResolve()
    {
        // A root the sandbox cannot vouch for must fail startup loudly — the alternative is
        // silently sharing its lexical form with link resolution never applied.
        Assert.Throws<InvalidOperationException>(
            static () => new PathSandbox([new AllowedRoot { Path = "\0", Writable = false }]));
    }

    [Fact]
    public void StartsWithARootThatDoesNotExistYet()
    {
        // Missing is not malformed: a configured root may be created after startup, and a
        // component that does not exist is not a link, so it still resolves.
        var missing = Path.Combine(Path.GetTempPath(), "ac-root-not-created-yet");
        Assert.False(Directory.Exists(missing));

        var sandbox = new PathSandbox([new AllowedRoot { Path = missing }]);

        Assert.True(sandbox.TryResolveForRead(Path.Combine(missing, "file.txt"), out _));
    }

    [Fact]
    public void SharingTheWholeFileSystemReachesPathsUnderIt()
    {
        // A root is its own prefix boundary, but a file-system root already ends in a separator
        // and must not be given a second one.
        var fileSystemRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var sandbox = new PathSandbox([new AllowedRoot { Path = fileSystemRoot }]);

        Assert.True(sandbox.TryResolveForRead(Path.GetTempPath(), out _));
        Assert.True(sandbox.TryResolveForRead(fileSystemRoot, out _));
    }

    [Fact]
    public void NeverSharesTheDaemonsOwnState()
    {
        // The private key in there is this machine's identity: handing it out through a share
        // would hand out the ability to be this machine.
        var directory = Directory.CreateTempSubdirectory("ac-state-test-");
        try
        {
            var state = Path.Combine(directory.FullName, "state");
            Directory.CreateDirectory(state);
            var sandbox = new PathSandbox(
                [new AllowedRoot { Path = directory.FullName, Writable = true }], neverShared: state);

            Assert.False(sandbox.TryResolveForRead(Path.Combine(state, "daemon.key"), out _));
            Assert.False(sandbox.TryResolveForRead(state, out _));
            Assert.False(sandbox.TryResolveForWrite(Path.Combine(state, "daemon.key"), out _));

            // Everything else under the shared root is unaffected.
            Assert.True(sandbox.TryResolveForRead(Path.Combine(directory.FullName, "notes.txt"), out _));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BlocksReadsThroughASymlinkLeavingTheRoot()
    {
        using var layout = new LinkedLayout();
        Assert.SkipWhen(layout.Unsupported, "No symbolic links and no junctions available on this machine.");

        // The lexical path stays inside the root the whole way; only the link's target leaves it.
        Assert.False(layout.Sandbox.TryResolveForRead(Path.Combine(layout.Root, "escape", "secret.txt"), out _));
    }

    [Fact]
    public void BlocksWritesThroughASymlinkLeavingTheRoot()
    {
        using var layout = new LinkedLayout(writable: true);
        Assert.SkipWhen(layout.Unsupported, "No symbolic links and no junctions available on this machine.");

        Assert.False(layout.Sandbox.TryResolveForWrite(Path.Combine(layout.Root, "escape", "planted.txt"), out _));
    }

    [Fact]
    public void AllowsASymlinkThatStaysInsideTheRoot()
    {
        using var layout = new LinkedLayout();
        Assert.SkipWhen(layout.Unsupported, "No symbolic links and no junctions available on this machine.");

        // Resolving links must not turn into "no links allowed" — one pointing back inside the
        // shared folder is exactly as legitimate as the folder itself.
        var allowed = layout.Sandbox.TryResolveForRead(Path.Combine(layout.Root, "inside", "kept.txt"), out var resolved);

        Assert.True(allowed);
        Assert.Equal(Path.Combine(layout.Root, "shared", "kept.txt"), resolved);
    }

    [Fact]
    public void BlocksALinkWhoseTargetPathRunsThroughAnotherLink()
    {
        // The `ln -s /etc /share/sub; ln -s /share/sub/passwd /share/hop` escape: hop's target
        // string lies lexically inside the root, but its "sub" component is itself a link that
        // leaves it. Following the chain without re-walking the returned target trusts that
        // string and lets the kernel do the escaping at open time.
        var baseDir = Directory.CreateTempSubdirectory("ac-sandbox-hops-");
        try
        {
            var root = Path.Combine(baseDir.FullName, "root");
            var outside = Path.Combine(baseDir.FullName, "outside");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(outside, "loot"));
            File.WriteAllText(Path.Combine(outside, "loot", "secret.txt"), "secret");

            var created =
                TryCreateDirectoryLink(Path.Combine(root, "sub"), outside)
                && TryCreateDirectoryLink(Path.Combine(root, "hop"), Path.Combine(root, "sub", "loot"));
            Assert.SkipUnless(created, "No symbolic links and no junctions available on this machine.");

            var sandbox = new PathSandbox([new AllowedRoot { Path = root, Writable = true }]);

            Assert.False(sandbox.TryResolveForRead(Path.Combine(root, "hop"), out _));
            Assert.False(sandbox.TryResolveForRead(Path.Combine(root, "hop", "secret.txt"), out _));
            Assert.False(sandbox.TryResolveForWrite(Path.Combine(root, "hop", "planted.txt"), out _));
        }
        finally
        {
            DeleteLinkedTree(baseDir, Path.Combine(baseDir.FullName, "root", "sub"), Path.Combine(baseDir.FullName, "root", "hop"));
        }
    }

    [Fact]
    public void RefusesACircularLinkChain()
    {
        // Two links pointing at each other must exhaust the hop budget and be refused —
        // never spin the resolver forever.
        var baseDir = Directory.CreateTempSubdirectory("ac-sandbox-cycle-");
        try
        {
            var root = Path.Combine(baseDir.FullName, "root");
            Directory.CreateDirectory(root);
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");

            var created = TryCreateDirectoryLink(first, second) && TryCreateDirectoryLink(second, first);
            Assert.SkipUnless(created, "No symbolic links and no junctions available on this machine.");

            var sandbox = new PathSandbox([new AllowedRoot { Path = root, Writable = false }]);

            Assert.False(sandbox.TryResolveForRead(Path.Combine(first, "file.txt"), out _));
        }
        finally
        {
            DeleteLinkedTree(baseDir, Path.Combine(baseDir.FullName, "root", "first"), Path.Combine(baseDir.FullName, "root", "second"));
        }
    }

    [Fact]
    public void ReadOnlyRootAllowsReadsButRefusesWrites()
    {
        var sandbox = ReadOnlySandbox();
        var target = Path.Combine(Root, "file.txt");

        Assert.True(sandbox.TryResolveForRead(target, out _));
        Assert.False(sandbox.TryResolveForWrite(target, out _));
    }

    [Fact]
    public void WritableRootAllowsBoth()
    {
        var sandbox = WritableSandbox();
        var target = Path.Combine(Root, "file.txt");

        Assert.True(sandbox.TryResolveForRead(target, out _));
        Assert.True(sandbox.TryResolveForWrite(target, out var resolved));
        Assert.Equal(target, resolved);
    }

    [Fact]
    public void WritesResolveOnlyAgainstTheWritableRoot()
    {
        var readOnlyRoot = Path.Combine(Path.GetTempPath(), "shared-read-only");
        var sandbox = new PathSandbox([
            new AllowedRoot { Path = readOnlyRoot, Writable = false },
            new AllowedRoot { Path = Root, Writable = true },
        ]);

        Assert.False(sandbox.TryResolveForWrite(Path.Combine(readOnlyRoot, "f.txt"), out _));
        Assert.True(sandbox.TryResolveForWrite(Path.Combine(Root, "f.txt"), out _));
    }

    /// <summary>
    /// Deletes the given links first (a link must never take the directory it points at with
    /// it, and a dangling one fails Exists checks), then the whole temp tree.
    /// </summary>
    private static void DeleteLinkedTree(DirectoryInfo baseDir, params string[] links)
    {
        foreach (var link in links)
        {
            try
            {
                Directory.Delete(link);
            }
            catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                // Never created (unsupported machine) or already gone; the recursive delete
                // below still sweeps whatever exists.
            }
        }

        baseDir.Delete(recursive: true);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows only permits symbolic links under Developer Mode or elevation. A junction
            // is the same kind of reparse point as far as the sandbox is concerned and needs
            // neither, so the escape still gets covered on a plain Windows dev machine.
            return OperatingSystem.IsWindows() && TryCreateJunction(link, target);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (mklink is null)
        {
            return false;
        }

        mklink.WaitForExit();
        return mklink.ExitCode == 0;
    }

    /// <summary>
    /// A shared root holding two links: one to a directory outside it, one to a sibling inside it.
    /// </summary>
    private sealed class LinkedLayout : IDisposable
    {
        private readonly DirectoryInfo _base;

        public LinkedLayout(bool writable = false)
        {
            this._base = Directory.CreateTempSubdirectory("ac-sandbox-links-");
            this.Root = Path.Combine(this._base.FullName, "root");
            var outside = Path.Combine(this._base.FullName, "outside");
            Directory.CreateDirectory(Path.Combine(this.Root, "shared"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            File.WriteAllText(Path.Combine(this.Root, "shared", "kept.txt"), "kept");

            this.Unsupported =
                !TryCreateDirectoryLink(Path.Combine(this.Root, "escape"), outside)
                || !TryCreateDirectoryLink(Path.Combine(this.Root, "inside"), Path.Combine(this.Root, "shared"));

            this.Sandbox = new PathSandbox([new AllowedRoot { Path = this.Root, Writable = writable }]);
        }

        public string Root { get; }

        public PathSandbox Sandbox { get; }

        public bool Unsupported { get; }

        public void Dispose()
        {
            DeleteLinkedTree(this._base, Path.Combine(this.Root, "escape"), Path.Combine(this.Root, "inside"));
        }
    }
}
