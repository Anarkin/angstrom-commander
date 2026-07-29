namespace AngstromCommander.Protocol;

/// <summary>Names of the methods the Server invokes on a connected Daemon over the relay socket.</summary>
public static class DaemonHubMethods
{
    public const string ListDirectory = "ListDirectory";
    public const string DownloadFile = "DownloadFile";
}
