using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public sealed class FileMutationHandlersTests : IDisposable
{
    private readonly string _writableRoot;
    private readonly string _readOnlyRoot;
    private readonly PathSandbox _sandbox;

    public FileMutationHandlersTests()
    {
        this._writableRoot = Directory.CreateTempSubdirectory("mut-rw-").FullName;
        this._readOnlyRoot = Directory.CreateTempSubdirectory("mut-ro-").FullName;
        this._sandbox = new PathSandbox(
        [
            new AllowedRoot { Path = this._writableRoot, Writable = true },
            new AllowedRoot { Path = this._readOnlyRoot, Writable = false },
        ]);
    }

    [Fact]
    public void CreatesADirectoryOnceAndOnlyOnce()
    {
        var handler = new CreateDirectoryHandler(this._sandbox);
        var path = Path.Combine(this._writableRoot, "fresh");

        Assert.Null(handler.Handle(new CreateDirectoryRequest(path)).Error);
        Assert.True(Directory.Exists(path));

        var again = handler.Handle(new CreateDirectoryRequest(path));
        Assert.Contains("already exists", again.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnlyRootsRefuseEveryMutation()
    {
        var existing = Path.Combine(this._readOnlyRoot, "keep.txt");
        File.WriteAllText(existing, "safe");

        var mkdir = new CreateDirectoryHandler(this._sandbox)
            .Handle(new CreateDirectoryRequest(Path.Combine(this._readOnlyRoot, "nope")));
        var delete = new DeleteEntryHandler(this._sandbox).Handle(new DeleteEntryRequest(existing));
        var move = new MoveEntryHandler(this._sandbox).Handle(new MoveEntryRequest(
            existing, Path.Combine(this._readOnlyRoot, "renamed.txt"), Overwrite: false));

        Assert.NotNull(mkdir.Error);
        Assert.NotNull(delete.Error);
        Assert.NotNull(move.Error);
        Assert.True(File.Exists(existing));
    }

    [Fact]
    public void MovingOutOfAWritableRootIntoAReadOnlyOneIsRefused()
    {
        var source = Path.Combine(this._writableRoot, "escapee.txt");
        File.WriteAllText(source, "x");

        var response = new MoveEntryHandler(this._sandbox).Handle(new MoveEntryRequest(
            source, Path.Combine(this._readOnlyRoot, "landed.txt"), Overwrite: false));

        Assert.NotNull(response.Error);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void RenamesAFileAndRefusesToReplaceWithoutConsent()
    {
        var handler = new MoveEntryHandler(this._sandbox);
        var source = Path.Combine(this._writableRoot, "old.txt");
        var target = Path.Combine(this._writableRoot, "new.txt");
        File.WriteAllText(source, "content");
        File.WriteAllText(target, "in the way");

        var refused = handler.Handle(new MoveEntryRequest(source, target, Overwrite: false));
        Assert.Contains("already exists", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("in the way", File.ReadAllText(target));

        var replaced = handler.Handle(new MoveEntryRequest(source, target, Overwrite: true));
        Assert.Null(replaced.Error);
        Assert.False(File.Exists(source));
        Assert.Equal("content", File.ReadAllText(target));
    }

    [Fact]
    public void MovesADirectoryButNeverOverANeighbour()
    {
        var handler = new MoveEntryHandler(this._sandbox);
        var source = Directory.CreateDirectory(Path.Combine(this._writableRoot, "folder")).FullName;
        File.WriteAllText(Path.Combine(source, "inside.txt"), "x");
        var occupied = Directory.CreateDirectory(Path.Combine(this._writableRoot, "occupied")).FullName;

        // Overwrite is a file affordance; directories never silently replace anything.
        var refused = handler.Handle(new MoveEntryRequest(source, occupied, Overwrite: true));
        Assert.NotNull(refused.Error);

        var moved = handler.Handle(new MoveEntryRequest(
            source, Path.Combine(this._writableRoot, "relocated"), Overwrite: false));
        Assert.Null(moved.Error);
        Assert.True(File.Exists(Path.Combine(this._writableRoot, "relocated", "inside.txt")));
    }

    [Fact]
    public void DeletesFilesAndWholeDirectories()
    {
        var handler = new DeleteEntryHandler(this._sandbox);
        var file = Path.Combine(this._writableRoot, "gone.txt");
        File.WriteAllText(file, "x");
        var tree = Directory.CreateDirectory(Path.Combine(this._writableRoot, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, "leaf.txt"), "x");

        Assert.Null(handler.Handle(new DeleteEntryRequest(file)).Error);
        Assert.Null(handler.Handle(new DeleteEntryRequest(tree)).Error);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(tree));

        var nothing = handler.Handle(new DeleteEntryRequest(Path.Combine(this._writableRoot, "ghost")));
        Assert.Contains("Nothing exists", nothing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedRootItselfCanNeverBeDeletedOrRenamed()
    {
        var deleted = new DeleteEntryHandler(this._sandbox).Handle(new DeleteEntryRequest(this._writableRoot));
        var renamed = new MoveEntryHandler(this._sandbox).Handle(new MoveEntryRequest(
            this._writableRoot, this._writableRoot + "-renamed", Overwrite: false));

        Assert.NotNull(deleted.Error);
        Assert.NotNull(renamed.Error);
        Assert.True(Directory.Exists(this._writableRoot));
    }

    [Fact]
    public void DeletingALinkRemovesTheLinkAndNeverItsTarget()
    {
        var target = Directory.CreateDirectory(Path.Combine(this._writableRoot, "real")).FullName;
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep me");
        var link = Path.Combine(this._writableRoot, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symlink creation needs privileges this runner does not have; the semantics
            // are still covered by the ReparsePoint branch, just not exercised here.
            return;
        }

        var response = new DeleteEntryHandler(this._sandbox).Handle(new DeleteEntryRequest(link));

        Assert.Null(response.Error);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    public void Dispose()
    {
        Directory.Delete(this._writableRoot, recursive: true);
        Directory.Delete(this._readOnlyRoot, recursive: true);
    }
}
