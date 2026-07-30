using AngstromCommander.Server.Relay;

namespace AngstromCommander.Server.Tests.Relay;

public class FileTransferRegistryTests
{
    private static readonly Guid Sender = Guid.NewGuid();
    private static readonly Guid Receiver = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    [Fact]
    public void OnlyTheMachineAskedToSendMayWrite()
    {
        var registry = new FileTransferRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        Assert.True(registry.TryGetForWriter(transferId, Sender, out _));
        Assert.False(registry.TryGetForWriter(transferId, Receiver, out _));
        Assert.False(registry.TryGetForWriter(transferId, Stranger, out _));
    }

    [Fact]
    public void OnlyTheMachineAskedToReceiveMayRead()
    {
        var registry = new FileTransferRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        Assert.True(registry.TryGetForReader(transferId, Receiver, out _));
        Assert.False(registry.TryGetForReader(transferId, Sender, out _));
        Assert.False(registry.TryGetForReader(transferId, Stranger, out _));
    }

    [Fact]
    public void OnlyTheReceiverMayReportCompletion()
    {
        var registry = new FileTransferRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);

        Assert.False(registry.TryComplete(transferId, Stranger, error: null));
        Assert.True(registry.TryComplete(transferId, Receiver, error: null));
    }

    [Fact]
    public void AnUnknownTransferIsNobodys()
    {
        var registry = new FileTransferRegistry();

        Assert.False(registry.TryGetForWriter(Guid.NewGuid(), Sender, out _));
        Assert.False(registry.TryGetForReader(Guid.NewGuid(), Receiver, out _));
        Assert.False(registry.TryGetChannel(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task ADisconnectingReceiverReleasesTheWaitingSender()
    {
        var registry = new FileTransferRegistry();
        var transferId = Create(registry, writerRegistrationId: null, readerRegistrationId: Receiver);
        var completion = registry.Completion(transferId);

        registry.AbandonFor(Receiver);

        Assert.NotNull(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ADisconnectingSenderFailsTheStreamRatherThanEndingIt()
    {
        var registry = new FileTransferRegistry();
        var transferId = Create(registry, writerRegistrationId: Sender, readerRegistrationId: null);
        Assert.True(registry.TryGetChannel(transferId, out var channel));

        registry.AbandonFor(Sender);

        // A plain completion would read as a clean end of file, and the caller would report a
        // truncated download as a success.
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync())
            {
                Assert.Empty(chunk);
            }
        });
    }

    [Fact]
    public void AbandoningLeavesOtherMachinesTransfersAlone()
    {
        var registry = new FileTransferRegistry();
        var mine = Create(registry, writerRegistrationId: Sender, readerRegistrationId: Receiver);
        var theirs = Create(registry, writerRegistrationId: Stranger, readerRegistrationId: null);

        registry.AbandonFor(Sender);

        Assert.False(registry.TryGetChannel(mine, out _));
        Assert.True(registry.TryGetChannel(theirs, out _));
    }

    [Fact]
    public void OneUserCannotOpenTransfersWithoutEnd()
    {
        // Every live transfer holds a buffer, on a Server shared with everyone else's machines.
        var registry = new FileTransferRegistry();
        var greedy = Guid.NewGuid();

        var opened = Enumerable.Range(0, 64)
            .Select(_ => registry.TryCreate(greedy, writerRegistrationId: Sender, readerRegistrationId: null))
            .ToList();

        Assert.Contains(opened, static transferId => transferId is null);

        // And one user filling up says nothing about anybody else.
        Assert.NotNull(registry.TryCreate(Guid.NewGuid(), writerRegistrationId: Sender, readerRegistrationId: null));
    }

    [Fact]
    public void FinishedTransfersFreeTheirPlaceInTheAllowance()
    {
        var registry = new FileTransferRegistry();
        var user = Guid.NewGuid();
        var opened = new List<Guid>();
        while (registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null) is Guid transferId)
        {
            opened.Add(transferId);
        }

        registry.Remove(opened[0]);

        Assert.NotNull(registry.TryCreate(user, writerRegistrationId: Sender, readerRegistrationId: null));
    }

    private static Guid Create(FileTransferRegistry registry, Guid? writerRegistrationId, Guid? readerRegistrationId)
    {
        var transferId = registry.TryCreate(Guid.NewGuid(), writerRegistrationId, readerRegistrationId);
        Assert.NotNull(transferId);
        return transferId.Value;
    }
}
