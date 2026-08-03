using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

internal sealed class DeleteEntryHandler(PathSandbox sandbox)
{
    public FileOperationResponse Handle(DeleteEntryRequest request)
    {
        if (!sandbox.TryResolveForMutation(request.Path, out var resolvedPath))
        {
            return FileOperationResponse.ForError("Path is not inside a writable root.");
        }

        try
        {
            if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
                return FileOperationResponse.Success;
            }

            if (!Directory.Exists(resolvedPath))
            {
                return FileOperationResponse.ForError("Nothing exists at that path.");
            }

            // A link to a directory is deleted as the link; recursing would delete the
            // tree it points at, which may live outside every root.
            var isLink = File.GetAttributes(resolvedPath).HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(resolvedPath, recursive: !isLink);
            return FileOperationResponse.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FileOperationResponse.ForError(ex.Message);
        }
    }
}
