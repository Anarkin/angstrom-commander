using System.Collections.Concurrent;

namespace AngstromCommander.Server.Relay;

internal sealed class InMemoryDaemonConnectionRegistry : IDaemonConnectionRegistry
{
    private readonly ConcurrentDictionary<string, string> _connections = new();

    public void Register(string daemonId, string connectionId)
    {
        this._connections[daemonId] = connectionId;
    }

    public void Unregister(string daemonId, string connectionId)
    {
        // Only remove if this connection still owns the entry — a reconnect may have
        // re-registered the Daemon under a new connection before the old one closed.
        this._connections.TryRemove(KeyValuePair.Create(daemonId, connectionId));
    }

    public bool TryGetConnection(string daemonId, out string connectionId)
    {
        return this._connections.TryGetValue(daemonId, out connectionId!);
    }
}
