namespace AngstromCommander.Protocol;

/// <summary>Names of the methods the Server invokes on a connected Daemon over the relay socket.</summary>
public static class DaemonHubMethods
{
    public const string ListRoots = "ListRoots";
    public const string ListDirectory = "ListDirectory";
    public const string DownloadFile = "DownloadFile";
    public const string UploadFile = "UploadFile";

    // Best-effort courtesy push when the user unpairs a connected machine, so it can
    // drop its dead registration immediately instead of discovering the refusal on its
    // next reconnect. No payload, no response.
    public const string Revoked = "Revoked";
}
