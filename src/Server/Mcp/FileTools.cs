using System.ComponentModel;
using AngstromCommander.Protocol;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Relay;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AngstromCommander.Server.Mcp;

/// <summary>
/// The MCP spelling of the file-op surface — thin adapters over the same
/// <see cref="DaemonRelayOperations"/> the REST endpoints use, exactly as the
/// architecture demands: one tool surface, one authorization path, no exceptions.
/// Byte-carrying operations (download, upload) are deliberately absent for now:
/// bytes do not belong base64-packed inside JSON-RPC.
/// </summary>
[McpServerToolType]
internal sealed class FileTools(DaemonRelayOperations operations, IHttpContextAccessor httpContext)
{
    [McpServerTool(Name = "list_machines")]
    [Description("The user's paired machines with online status. Every other tool needs a registrationId from here.")]
    public async Task<IReadOnlyList<MachineResponse>> ListMachines(CancellationToken cancellationToken)
    {
        return await operations.ListMachinesAsync(this.UserId, cancellationToken);
    }

    [McpServerTool(Name = "list_roots")]
    [Description("The folders a machine shares and whether each allows writes. Paths outside these roots are refused.")]
    public async Task<IReadOnlyList<SharedRoot>> ListRoots(
        [Description("The machine's registrationId, from list_machines.")] Guid registrationId,
        CancellationToken cancellationToken)
    {
        return Unwrap(await operations.ListRootsAsync(this.UserId, registrationId, cancellationToken));
    }

    [McpServerTool(Name = "list_directory")]
    [Description("Lists a directory on a machine: names, sizes, modified timestamps.")]
    public async Task<IReadOnlyList<DirectoryEntry>> ListDirectory(
        [Description("The machine's registrationId, from list_machines.")] Guid registrationId,
        [Description("Absolute path inside one of the machine's shared roots.")] string path,
        CancellationToken cancellationToken)
    {
        return Unwrap(await operations.ListDirectoryAsync(this.UserId, registrationId, path, cancellationToken));
    }

    [McpServerTool(Name = "create_directory")]
    [Description("Creates a new directory (parents included) inside a writable root.")]
    public async Task<string> CreateDirectory(
        [Description("The machine's registrationId, from list_machines.")] Guid registrationId,
        [Description("Absolute path of the directory to create, inside a writable root.")] string path,
        CancellationToken cancellationToken)
    {
        Unwrap(await operations.CreateDirectoryAsync(this.UserId, registrationId, path, cancellationToken));
        return $"Created {path}.";
    }

    [McpServerTool(Name = "move_entry")]
    [Description("Moves or renames a file or directory within a machine's writable roots. Rename is a move with the same parent.")]
    public async Task<string> MoveEntry(
        [Description("The machine's registrationId, from list_machines.")] Guid registrationId,
        [Description("Absolute path of the existing file or directory.")] string sourcePath,
        [Description("Absolute path it should end up at.")] string targetPath,
        [Description("Replace an existing FILE at the target. Directories never replace anything.")] bool overwrite,
        CancellationToken cancellationToken)
    {
        Unwrap(await operations.MoveEntryAsync(
            this.UserId, registrationId, sourcePath, targetPath, overwrite, cancellationToken));
        return $"Moved {sourcePath} to {targetPath}.";
    }

    [McpServerTool(Name = "delete_entry")]
    [Description("Deletes a file, or a directory with everything in it. There is no undo; ask the user before calling this.")]
    public async Task<string> DeleteEntry(
        [Description("The machine's registrationId, from list_machines.")] Guid registrationId,
        [Description("Absolute path of the file or directory to delete, inside a writable root.")] string path,
        CancellationToken cancellationToken)
    {
        Unwrap(await operations.DeleteEntryAsync(this.UserId, registrationId, path, cancellationToken));
        return $"Deleted {path}.";
    }

    private static T Unwrap<T>(RelayResult<T> result)
    {
        return result.Failure switch
        {
            RelayFailure.None => result.Value!,
            RelayFailure.NoSuchMachine => throw new McpException("No such machine."),
            RelayFailure.MachineOffline => throw new McpException("The machine is not connected right now."),
            RelayFailure.MachineSilent => throw new McpException("The machine did not answer."),
            _ => throw new McpException(result.RefusalDetail ?? "The machine refused the operation."),
        };
    }

    private Guid UserId =>
        Guid.Parse(httpContext.HttpContext!.User.FindFirst(AuthClaims.Subject)!.Value);
}
