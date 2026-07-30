using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

internal sealed class DownloadFileHandler(PathSandbox sandbox)
{
    private const int FileBufferSize = 64 * 1024;

    /// <summary>
    /// Validates the request against the sandbox and opens the file for streaming.
    /// On success the caller owns the returned stream and must dispose it.
    /// </summary>
    public DownloadFileResponse Open(DownloadFileRequest request, out FileStream? stream)
    {
        stream = null;
        if (!sandbox.TryResolveForRead(request.Path, out var resolvedPath))
        {
            return DownloadFileResponse.ForError("Path is outside the allowed roots.");
        }

        var info = new FileInfo(resolvedPath);
        if (!info.Exists)
        {
            return DownloadFileResponse.ForError("File does not exist.");
        }

        try
        {
            stream = new FileStream(
                resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DownloadFileResponse.ForError(ex.Message);
        }

        return DownloadFileResponse.ForFile(info.Name, info.Length);
    }
}
