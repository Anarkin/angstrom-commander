namespace AngstromCommander.Protocol;

/// <summary>Names of the hub methods a Daemon invokes on the Server over the relay socket.</summary>
public static class ServerHubMethods
{
    /// <summary>Client-to-server streaming: the Daemon pushes the bytes of a requested file.</summary>
    public const string UploadFileChunks = "UploadFileChunks";

    /// <summary>Server-to-client streaming: the Daemon pulls the bytes of a file being sent to it.</summary>
    public const string DownloadFileChunks = "DownloadFileChunks";

    /// <summary>Reports that a pulled transfer finished writing (or why it failed).</summary>
    public const string CompleteTransfer = "CompleteTransfer";
}
