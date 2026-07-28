using AngstromCommander.Server.Relay;

namespace AngstromCommander.Server.Tests.Relay;

public class InMemoryDaemonConnectionRegistryTests
{
    [Fact]
    public void RegisterThenTryGetReturnsConnection()
    {
        var registry = new InMemoryDaemonConnectionRegistry();

        registry.Register("daemon-1", "conn-a");

        Assert.True(registry.TryGetConnection("daemon-1", out var connectionId));
        Assert.Equal("conn-a", connectionId);
    }

    [Fact]
    public void UnregisterRemovesEntry()
    {
        var registry = new InMemoryDaemonConnectionRegistry();
        registry.Register("daemon-1", "conn-a");

        registry.Unregister("daemon-1", "conn-a");

        Assert.False(registry.TryGetConnection("daemon-1", out _));
    }

    [Fact]
    public void StaleUnregisterKeepsNewerRegistration()
    {
        var registry = new InMemoryDaemonConnectionRegistry();
        registry.Register("daemon-1", "conn-old");
        registry.Register("daemon-1", "conn-new");

        registry.Unregister("daemon-1", "conn-old");

        Assert.True(registry.TryGetConnection("daemon-1", out var connectionId));
        Assert.Equal("conn-new", connectionId);
    }

    [Fact]
    public void TryGetUnknownDaemonReturnsFalse()
    {
        var registry = new InMemoryDaemonConnectionRegistry();

        Assert.False(registry.TryGetConnection("nope", out _));
    }
}
