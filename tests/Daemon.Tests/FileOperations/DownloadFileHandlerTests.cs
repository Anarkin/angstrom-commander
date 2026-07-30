using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.Tests.FileOperations;

public class DownloadFileHandlerTests
{
    [Fact]
    public void OpensFileWithMetadata()
    {
        var root = Directory.CreateTempSubdirectory("ac-download-test-");
        try
        {
            var path = Path.Combine(root.FullName, "payload.bin");
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            var handler = new DownloadFileHandler(new PathSandbox([new AllowedRoot { Path = root.FullName }]));

            var response = handler.Open(new DownloadFileRequest(Guid.NewGuid(), path), out var stream);

            Assert.Null(response.Error);
            Assert.Equal("payload.bin", response.FileName);
            Assert.Equal(5, response.SizeBytes);
            Assert.NotNull(stream);
            using (stream)
            {
                Assert.Equal(1, stream.ReadByte());
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RejectsPathOutsideSandbox()
    {
        var handler = new DownloadFileHandler(new PathSandbox([]));

        var response = handler.Open(new DownloadFileRequest(Guid.NewGuid(), Path.GetTempPath()), out var stream);

        Assert.Null(stream);
        Assert.NotNull(response.Error);
        stream?.Dispose();
    }

    [Fact]
    public void ReportsMissingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ac-missing-" + Guid.NewGuid());
        var handler = new DownloadFileHandler(new PathSandbox([new AllowedRoot { Path = root }]));

        var response = handler.Open(
            new DownloadFileRequest(Guid.NewGuid(), Path.Combine(root, "nope.txt")), out var stream);

        Assert.Null(stream);
        Assert.NotNull(response.Error);
        stream?.Dispose();
    }
}
