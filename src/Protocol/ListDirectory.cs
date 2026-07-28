namespace AngstromCommander.Protocol;

public sealed record ListDirectoryRequest(string Path);

public sealed record DirectoryEntry(string Name, bool IsDirectory, long? SizeBytes, DateTimeOffset ModifiedAt);

public sealed record ListDirectoryResponse(IReadOnlyList<DirectoryEntry>? Entries, string? Error)
{
    public static ListDirectoryResponse ForEntries(IReadOnlyList<DirectoryEntry> entries)
    {
        return new(entries, null);
    }

    public static ListDirectoryResponse ForError(string error)
    {
        return new(null, error);
    }
}
