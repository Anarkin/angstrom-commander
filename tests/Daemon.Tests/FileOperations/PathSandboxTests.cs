using AngstromCommander.Daemon.FileOperations;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public class PathSandboxTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sandbox-root");

    [Fact]
    public void AllowsPathInsideRoot()
    {
        var sandbox = new PathSandbox([Root]);

        var allowed = sandbox.TryResolve(Path.Combine(Root, "sub", "file.txt"), out var resolved);

        Assert.True(allowed);
        Assert.Equal(Path.Combine(Root, "sub", "file.txt"), resolved);
    }

    [Fact]
    public void AllowsRootItself()
    {
        var sandbox = new PathSandbox([Root]);

        Assert.True(sandbox.TryResolve(Root, out _));
    }

    [Fact]
    public void BlocksPathOutsideRoots()
    {
        var sandbox = new PathSandbox([Root]);

        Assert.False(sandbox.TryResolve(Path.Combine(Path.GetTempPath(), "elsewhere"), out _));
    }

    [Fact]
    public void BlocksTraversalEscapingRoot()
    {
        var sandbox = new PathSandbox([Root]);

        Assert.False(sandbox.TryResolve(Path.Combine(Root, "..", "evil"), out _));
    }

    [Fact]
    public void ResolvesTraversalStayingInsideRoot()
    {
        var sandbox = new PathSandbox([Root]);

        var allowed = sandbox.TryResolve(Path.Combine(Root, "sub", "..", "file.txt"), out var resolved);

        Assert.True(allowed);
        Assert.Equal(Path.Combine(Root, "file.txt"), resolved);
    }

    [Fact]
    public void BlocksSiblingSharingRootPrefix()
    {
        var sandbox = new PathSandbox([Root]);

        Assert.False(sandbox.TryResolve(Root + "2", out _));
    }

    [Fact]
    public void BlocksEverythingWhenNoRootsConfigured()
    {
        var sandbox = new PathSandbox([]);

        Assert.False(sandbox.TryResolve(Root, out _));
    }
}
