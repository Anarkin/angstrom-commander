using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public class UploadFileHandlerTests
{
    [Fact]
    public void AcceptsNewFileInWritableRoot()
    {
        var root = Directory.CreateTempSubdirectory("ac-upload-test-");
        try
        {
            var handler = Handler(root.FullName, writable: true);
            var target = Path.Combine(root.FullName, "new.bin");

            var response = handler.Accept(Request(target), out var resolvedPath);

            Assert.Null(response.Error);
            Assert.Equal(target, resolvedPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RefusesReadOnlyRoot()
    {
        var root = Directory.CreateTempSubdirectory("ac-upload-test-");
        try
        {
            var handler = Handler(root.FullName, writable: false);

            var response = handler.Accept(Request(Path.Combine(root.FullName, "new.bin")), out _);

            Assert.NotNull(response.Error);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RefusesExistingFileUnlessOverwriteRequested()
    {
        var root = Directory.CreateTempSubdirectory("ac-upload-test-");
        try
        {
            var handler = Handler(root.FullName, writable: true);
            var target = Path.Combine(root.FullName, "existing.bin");
            File.WriteAllBytes(target, [1, 2, 3]);

            Assert.NotNull(handler.Accept(Request(target), out _).Error);
            Assert.Null(handler.Accept(Request(target, overwrite: true), out _).Error);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RefusesMissingContainingDirectory()
    {
        var root = Directory.CreateTempSubdirectory("ac-upload-test-");
        try
        {
            var handler = Handler(root.FullName, writable: true);

            var response = handler.Accept(Request(Path.Combine(root.FullName, "nope", "new.bin")), out _);

            Assert.NotNull(response.Error);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RefusesDirectoryAsTarget()
    {
        var root = Directory.CreateTempSubdirectory("ac-upload-test-");
        try
        {
            var handler = Handler(root.FullName, writable: true);

            var response = handler.Accept(Request(root.FullName), out _);

            Assert.NotNull(response.Error);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void OpenForWritingRefusesToClobberWithoutOverwrite()
    {
        var root = Directory.CreateTempSubdirectory("ac-upload-test-");
        try
        {
            var target = Path.Combine(root.FullName, "existing.bin");
            File.WriteAllBytes(target, [1, 2, 3]);

            Assert.Throws<IOException>(() => UploadFileHandler.OpenForWriting(target, overwrite: false).Dispose());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static UploadFileHandler Handler(string root, bool writable)
    {
        return new UploadFileHandler(new PathSandbox([new AllowedRoot { Path = root, Writable = writable }]));
    }

    private static UploadFileRequest Request(string path, bool overwrite = false)
    {
        return new UploadFileRequest(Guid.NewGuid(), path, overwrite);
    }
}
