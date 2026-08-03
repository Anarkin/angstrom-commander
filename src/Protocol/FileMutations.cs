namespace AngstromCommander.Protocol;

public sealed record CreateDirectoryRequest(string Path);

// Rename is a move with the same parent: one primitive covers both.
public sealed record MoveEntryRequest(string SourcePath, string TargetPath, bool Overwrite);

public sealed record DeleteEntryRequest(string Path);

/// <summary>One response shape for the mutations: they either work or explain why not.</summary>
public sealed record FileOperationResponse(string? Error)
{
    public static FileOperationResponse Success { get; } = new((string?)null);

    public static FileOperationResponse ForError(string error)
    {
        return new(error);
    }
}
