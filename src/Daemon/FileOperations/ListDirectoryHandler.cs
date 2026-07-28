using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

internal sealed class ListDirectoryHandler(PathSandbox sandbox)
{
    public ListDirectoryResponse Handle(ListDirectoryRequest request)
    {
        if (!sandbox.TryResolve(request.Path, out var resolvedPath))
        {
            return ListDirectoryResponse.ForError("Path is outside the allowed roots.");
        }

        try
        {
            var directory = new DirectoryInfo(resolvedPath);
            if (!directory.Exists)
            {
                return ListDirectoryResponse.ForError("Directory does not exist.");
            }

            var entries = directory
                .EnumerateFileSystemInfos()
                .Select(static info => new DirectoryEntry(
                    info.Name,
                    IsDirectory: info is DirectoryInfo,
                    SizeBytes: info is FileInfo file ? file.Length : null,
                    ModifiedAt: info.LastWriteTimeUtc))
                .ToList();

            return ListDirectoryResponse.ForEntries(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ListDirectoryResponse.ForError(ex.Message);
        }
    }
}
