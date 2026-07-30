namespace AngstromCommander.Protocol;

/// <summary>
/// Asks a Daemon to receive a file. The Daemon validates and answers immediately, then pulls the
/// bytes itself as a separate streaming invocation tagged with <paramref name="TransferId"/> —
/// SignalR cannot push a stream to a client, so the direction of initiation is inverted here
/// compared to a download.
/// </summary>
public sealed record UploadFileRequest(Guid TransferId, string Path, bool Overwrite);

public sealed record UploadFileResponse(string? Error)
{
    public static UploadFileResponse ForSuccess()
    {
        return new((string?)null);
    }

    public static UploadFileResponse ForError(string error)
    {
        return new(error);
    }
}
