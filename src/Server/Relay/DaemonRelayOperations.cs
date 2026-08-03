using AngstromCommander.Protocol;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Relay;

/// <summary>Why a relay operation produced no value.</summary>
internal enum RelayFailure
{
    None,
    NoSuchMachine,
    MachineOffline,
    MachineSilent,
    DaemonRefused,
}

internal sealed record RelayResult<T>(RelayFailure Failure, T? Value, string? RefusalDetail)
{
    public static RelayResult<T> Ok(T value)
    {
        return new(RelayFailure.None, value, null);
    }

    public static RelayResult<T> Fail(RelayFailure failure)
    {
        return new(failure, default, null);
    }

    public static RelayResult<T> Refused(string detail)
    {
        return new(RelayFailure.DaemonRefused, default, detail);
    }
}

/// <summary>
/// The one implementation of the request/response relay operations, shared by the REST
/// endpoints and the MCP tools — MCP is a protocol adapter over this surface, never a
/// second implementation (ARCHITECTURE.md). The streaming transfers stay with their
/// endpoints; nothing here carries file bytes.
/// </summary>
internal sealed class DaemonRelayOperations(
    AppDbContext db,
    IDaemonConnectionRegistry registry,
    IHubContext<DaemonHub> hub)
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<MachineResponse>> ListMachinesAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var registrations = await db.DaemonRegistrations.AsNoTracking()
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .OrderBy(static r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        return registrations
            .Select(r => new MachineResponse(
                r.Id,
                r.DisplayName,
                r.Platform,
                r.CreatedAt,
                r.LastSeenAt,
                Online: registry.TryGetConnection(r.Id.ToString(), out _)))
            .ToList();
    }

    public Task<RelayResult<IReadOnlyList<SharedRoot>>> ListRootsAsync(
        Guid userId, Guid registrationId, CancellationToken cancellationToken)
    {
        return this.InvokeAsync<ListRootsResponse, IReadOnlyList<SharedRoot>>(
            userId, registrationId, DaemonHubMethods.ListRoots, request: null,
            static response => (response.Roots, null), cancellationToken);
    }

    public Task<RelayResult<IReadOnlyList<DirectoryEntry>>> ListDirectoryAsync(
        Guid userId, Guid registrationId, string path, CancellationToken cancellationToken)
    {
        return this.InvokeAsync<ListDirectoryResponse, IReadOnlyList<DirectoryEntry>>(
            userId, registrationId, DaemonHubMethods.ListDirectory, new ListDirectoryRequest(path),
            static response => (
                response.Entries,
                response.Error ?? (response.Entries is null ? "The machine sent no directory listing." : null)),
            cancellationToken);
    }

    public Task<RelayResult<bool>> CreateDirectoryAsync(
        Guid userId, Guid registrationId, string path, CancellationToken cancellationToken)
    {
        return this.MutateAsync(
            userId, registrationId, DaemonHubMethods.CreateDirectory,
            new CreateDirectoryRequest(path), cancellationToken);
    }

    public Task<RelayResult<bool>> MoveEntryAsync(
        Guid userId, Guid registrationId, string sourcePath, string targetPath, bool overwrite,
        CancellationToken cancellationToken)
    {
        return this.MutateAsync(
            userId, registrationId, DaemonHubMethods.MoveEntry,
            new MoveEntryRequest(sourcePath, targetPath, overwrite), cancellationToken);
    }

    public Task<RelayResult<bool>> DeleteEntryAsync(
        Guid userId, Guid registrationId, string path, CancellationToken cancellationToken)
    {
        return this.MutateAsync(
            userId, registrationId, DaemonHubMethods.DeleteEntry,
            new DeleteEntryRequest(path), cancellationToken);
    }

    private Task<RelayResult<bool>> MutateAsync(
        Guid userId, Guid registrationId, string method, object request, CancellationToken cancellationToken)
    {
        return this.InvokeAsync<FileOperationResponse, bool>(
            userId, registrationId, method, request,
            static response => (true, response.Error), cancellationToken);
    }

    private async Task<RelayResult<T>> InvokeAsync<TResponse, T>(
        Guid userId,
        Guid registrationId,
        string method,
        object? request,
        Func<TResponse, (T? Value, string? Error)> map,
        CancellationToken cancellationToken)
    {
        var owns = await db.DaemonRegistrations.AsNoTracking().AnyAsync(
            r => r.Id == registrationId && r.UserId == userId && r.RevokedAt == null,
            cancellationToken);
        if (!owns)
        {
            return RelayResult<T>.Fail(RelayFailure.NoSuchMachine);
        }

        if (!registry.TryGetConnection(registrationId.ToString(), out var connectionId))
        {
            return RelayResult<T>.Fail(RelayFailure.MachineOffline);
        }

        TResponse response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            var client = hub.Clients.Client(connectionId);
            response = request is null
                ? await client.InvokeAsync<TResponse>(method, timeout.Token)
                : await client.InvokeAsync<TResponse>(method, request, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return RelayResult<T>.Fail(RelayFailure.MachineSilent);
        }

        var (value, error) = map(response);
        return error is not null ? RelayResult<T>.Refused(error) : RelayResult<T>.Ok(value!);
    }
}
