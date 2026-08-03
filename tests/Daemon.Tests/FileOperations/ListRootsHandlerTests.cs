using AngstromCommander.Daemon.FileOperations;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public sealed class ListRootsHandlerTests : IDisposable
{
    private readonly string _readOnlyRoot;
    private readonly string _writableRoot;

    public ListRootsHandlerTests()
    {
        this._readOnlyRoot = Directory.CreateTempSubdirectory("roots-ro-").FullName;
        this._writableRoot = Directory.CreateTempSubdirectory("roots-rw-").FullName;
    }

    [Fact]
    public void ReportsTheResolvedRootsWithTheirWritability()
    {
        var sandbox = new PathSandbox(
        [
            new AllowedRoot { Path = this._readOnlyRoot, Writable = false },
            new AllowedRoot { Path = this._writableRoot, Writable = true },
        ]);
        var handler = new ListRootsHandler(sandbox);

        var response = handler.Handle();

        Assert.Collection(
            response.Roots,
            root =>
            {
                Assert.Equal(this._readOnlyRoot, root.Path, ignoreCase: OperatingSystem.IsWindows());
                Assert.False(root.Writable);
            },
            root =>
            {
                Assert.Equal(this._writableRoot, root.Path, ignoreCase: OperatingSystem.IsWindows());
                Assert.True(root.Writable);
            });
    }

    [Fact]
    public void AMachineSharingNothingSaysSo()
    {
        var handler = new ListRootsHandler(new PathSandbox([]));

        Assert.Empty(handler.Handle().Roots);
    }

    public void Dispose()
    {
        Directory.Delete(this._readOnlyRoot, recursive: true);
        Directory.Delete(this._writableRoot, recursive: true);
    }
}
