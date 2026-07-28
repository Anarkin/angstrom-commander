namespace AngstromCommander.Server.Relay;

/// <summary>
/// Maps a Daemon id to the SignalR connection that Daemon currently holds open.
/// Socket ownership is the Server's only per-replica state; behind this interface the
/// in-process map can be swapped for a distributed registry (Daemon → owning replica)
/// when the Server scales past one replica — see ARCHITECTURE.md § Scaling.
/// </summary>
internal interface IDaemonConnectionRegistry
{
    void Register(string daemonId, string connectionId);

    void Unregister(string daemonId, string connectionId);

    bool TryGetConnection(string daemonId, out string connectionId);
}
