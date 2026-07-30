using AngstromCommander.Protocol;

namespace AngstromCommander.Daemon.FileOperations;

internal sealed class UploadFileHandler(PathSandbox sandbox)
{
    private const int FileBufferSize = 64 * 1024;

    /// <summary>
    /// Checks whether this machine will accept the upload and resolves where it lands. Parent
    /// directories are never created implicitly — an upload writes a file, it does not reshape
    /// the tree. Opening the file is deliberately left to <see cref="OpenForWriting"/> so the
    /// stream's lifetime belongs entirely to whoever pumps the bytes.
    /// </summary>
    public UploadFileResponse Accept(UploadFileRequest request, out string resolvedPath)
    {
        if (!sandbox.TryResolveForWrite(request.Path, out resolvedPath))
        {
            return UploadFileResponse.ForError("Path is not inside a writable root.");
        }

        if (Directory.Exists(resolvedPath))
        {
            return UploadFileResponse.ForError("Path is a directory.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(resolvedPath)))
        {
            return UploadFileResponse.ForError("The containing directory does not exist.");
        }

        if (!request.Overwrite && File.Exists(resolvedPath))
        {
            return UploadFileResponse.ForError("The file already exists.");
        }

        return UploadFileResponse.ForSuccess();
    }

    /// <summary>Opens an accepted destination. Not overwriting is enforced atomically here too.</summary>
    public static FileStream OpenForWriting(string resolvedPath, bool overwrite)
    {
        return new FileStream(
            resolvedPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            useAsync: true);
    }
}
