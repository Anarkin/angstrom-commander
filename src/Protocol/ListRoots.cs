namespace AngstromCommander.Protocol;

/// <summary>A directory a machine shares, and whether writes are allowed into it.</summary>
public sealed record SharedRoot(string Path, bool Writable);

public sealed record ListRootsResponse(IReadOnlyList<SharedRoot> Roots);
