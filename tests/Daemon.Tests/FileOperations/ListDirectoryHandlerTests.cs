using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public class ListDirectoryHandlerTests
{
    [Fact]
    public void ListsFilesAndDirectoriesWithMetadata()
    {
        var root = Directory.CreateTempSubdirectory("ac-handler-test-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "hello.txt"), "hi");
            Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
            var handler = new ListDirectoryHandler(new PathSandbox([root.FullName]));

            var response = handler.Handle(new ListDirectoryRequest(root.FullName));

            Assert.Null(response.Error);
            Assert.NotNull(response.Entries);
            var file = Assert.Single(response.Entries, static e => !e.IsDirectory);
            Assert.Equal("hello.txt", file.Name);
            Assert.Equal(2, file.SizeBytes);
            var directory = Assert.Single(response.Entries, static e => e.IsDirectory);
            Assert.Equal("sub", directory.Name);
            Assert.Null(directory.SizeBytes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RejectsPathOutsideSandbox()
    {
        var handler = new ListDirectoryHandler(new PathSandbox([]));

        var response = handler.Handle(new ListDirectoryRequest(Path.GetTempPath()));

        Assert.Null(response.Entries);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public void ReportsMissingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ac-does-not-exist-" + Guid.NewGuid());
        var handler = new ListDirectoryHandler(new PathSandbox([root]));

        var response = handler.Handle(new ListDirectoryRequest(root));

        Assert.Null(response.Entries);
        Assert.NotNull(response.Error);
    }
}
