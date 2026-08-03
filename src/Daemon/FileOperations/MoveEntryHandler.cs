using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

internal sealed class MoveEntryHandler(PathSandbox sandbox)
{
    public FileOperationResponse Handle(MoveEntryRequest request)
    {
        // Both ends are mutations: the source stops existing under its name, the target
        // starts to. Both therefore need a writable root — and both act on a link itself
        // rather than whatever it points at.
        if (!sandbox.TryResolveForMutation(request.SourcePath, out var sourcePath))
        {
            return FileOperationResponse.ForError("Source is not inside a writable root.");
        }

        if (!sandbox.TryResolveForMutation(request.TargetPath, out var targetPath))
        {
            return FileOperationResponse.ForError("Target is not inside a writable root.");
        }

        try
        {
            var sourceIsFile = File.Exists(sourcePath);
            var sourceIsDirectory = !sourceIsFile && Directory.Exists(sourcePath);
            if (!sourceIsFile && !sourceIsDirectory)
            {
                return FileOperationResponse.ForError("Nothing exists at the source path.");
            }

            var targetTaken = File.Exists(targetPath) || Directory.Exists(targetPath);
            if (targetTaken && (!request.Overwrite || sourceIsDirectory))
            {
                // Overwriting is for files, and only when the caller said so; a directory
                // never silently replaces anything.
                return FileOperationResponse.ForError("Something already exists at the target path.");
            }

            if (sourceIsFile)
            {
                File.Move(sourcePath, targetPath, request.Overwrite);
            }
            else
            {
                Directory.Move(sourcePath, targetPath);
            }

            return FileOperationResponse.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FileOperationResponse.ForError(ex.Message);
        }
    }
}
