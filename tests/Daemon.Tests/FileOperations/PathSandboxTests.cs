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
        if (layout.Unsupported)
        {
            // No symbolic links and no junctions available on this machine.
            return;
        }

        // The lexical path stays inside the root the whole way; only the link's target leaves it.
        Assert.False(layout.Sandbox.TryResolveForRead(Path.Combine(layout.Root, "escape", "secret.txt"), out _));
    }

    [Fact]
    public void BlocksWritesThroughASymlinkLeavingTheRoot()
    {
        using var layout = new LinkedLayout(writable: true);
        if (layout.Unsupported)
        {
            // No symbolic links and no junctions available on this machine.
            return;
        }

        Assert.False(layout.Sandbox.TryResolveForWrite(Path.Combine(layout.Root, "escape", "planted.txt"), out _));
    }

    [Fact]
    public void AllowsASymlinkThatStaysInsideTheRoot()
    {
        using var layout = new LinkedLayout();
        if (layout.Unsupported)
        {
            // No symbolic links and no junctions available on this machine.
            return;
        }

        // Resolving links must not turn into "no links allowed" — one pointing back inside the
        // shared folder is exactly as legitimate as the folder itself.
        var allowed = layout.Sandbox.TryResolveForRead(Path.Combine(layout.Root, "inside", "kept.txt"), out var resolved);

        Assert.True(allowed);
        Assert.Equal(Path.Combine(layout.Root, "shared", "kept.txt"), resolved);
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
            // Drop the links first: a recursive delete refuses to descend into a reparse point,
            // and deleting the link itself must not take the directory it points at with it.
            foreach (var link in new[] { "escape", "inside" })
            {
                var path = Path.Combine(this.Root, link);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
            }

            this._base.Delete(recursive: true);
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
    }
}
