using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

internal sealed class CreateDirectoryHandler(PathSandbox sandbox)
{
    public FileOperationResponse Handle(CreateDirectoryRequest request)
    {
        if (!sandbox.TryResolveForMutation(request.Path, out var resolvedPath))
        {
            return FileOperationResponse.ForError("Path is not inside a writable root.");
        }

        try
        {
            // CreateDirectory alone is silently idempotent; a user asking for a folder
            // that already exists deserves to hear that rather than a false success.
            if (Directory.Exists(resolvedPath) || File.Exists(resolvedPath))
            {
                return FileOperationResponse.ForError("Something already exists at that path.");
            }

            Directory.CreateDirectory(resolvedPath);
            return FileOperationResponse.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FileOperationResponse.ForError(ex.Message);
        }
    }
}
