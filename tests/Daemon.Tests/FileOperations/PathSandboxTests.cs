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
}
