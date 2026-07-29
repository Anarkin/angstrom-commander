namespace AngstromCommander.Protocol;

/// <summary>
/// Asks a Daemon to start streaming a file. The Daemon answers with metadata (or an
/// error) and then pushes the bytes as a separate streaming invocation tagged with
/// <paramref name="TransferId"/> — the response never carries file content itself.
/// </summary>
public sealed record DownloadFileRequest(Guid TransferId, string Path);

public sealed record DownloadFileResponse(string? FileName, long? SizeBytes, string? Error)
{
    public static DownloadFileResponse ForFile(string fileName, long sizeBytes)
    {
        return new(fileName, sizeBytes, null);
    }

    public static DownloadFileResponse ForError(string error)
    {
        return new(null, null, error);
    }
}
