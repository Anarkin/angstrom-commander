using AngstromCommander.Daemon.Relay;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace AngstromCommander.Daemon.Tests.Relay;

public class DaemonRelayServiceTests
{
    [Fact]
    public async Task AFileClosesEvenWhenItsTransferNeverStarts()
    {
        // A Daemon serving downloads all day cannot leak a handle per failed transfer, and the
        // chunk iterator is lazy — nothing reads it when the invocation fails outright.
        var directory = Directory.CreateTempSubdirectory("ac-pump-test-");
        var path = Path.Combine(directory.FullName, "payload.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
#pragma warning disable CA2000 // Closing it is what is under test, and PumpFileAsync is what does it.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
#pragma warning restore CA2000

        // Never started, so the invocation fails before a single chunk is read.
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost:1/hub/daemon"))
            .Build();

        await DaemonRelayService.PumpFileAsync(
            connection, Guid.NewGuid(), file, NullLogger.Instance, CancellationToken.None);

        Assert.False(file.CanRead);

        // On Windows this is the real proof: a still-open handle would refuse the delete.
        directory.Delete(recursive: true);
    }

    [Fact]
    public async Task AFailedTransferLeavesTheExistingDestinationUntouched()
    {
        // Overwrite consent is consent to replace the file with the new one — never a license
        // to destroy the old version before the new one has fully arrived.
        var directory = Directory.CreateTempSubdirectory("ac-pull-fault-");
        try
        {
            var destination = Path.Combine(directory.FullName, "target.bin");
            await File.WriteAllTextAsync(
                destination, "previous version", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() => DaemonRelayService.ReceiveIntoAsync(
                destination,
                overwrite: true,
                Guid.NewGuid(),
                ChunksThenFault(new IOException("socket dropped"), [1, 2, 3]),
                NullLogger.Instance,
                CancellationToken.None));

            Assert.Equal(
                "previous version",
                await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal([destination], Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AFailedTransferIntoANewFileLeavesNothingBehind()
    {
        var directory = Directory.CreateTempSubdirectory("ac-pull-fault-new-");
        try
        {
            var destination = Path.Combine(directory.FullName, "fresh.bin");

            await Assert.ThrowsAsync<IOException>(() => DaemonRelayService.ReceiveIntoAsync(
                destination,
                overwrite: false,
                Guid.NewGuid(),
                ChunksThenFault(new IOException("socket dropped"), [1, 2, 3]),
                NullLogger.Instance,
                CancellationToken.None));

            Assert.Empty(Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ACancelledTransferCleansUpItsPartialFile()
    {
        var directory = Directory.CreateTempSubdirectory("ac-pull-cancel-");
        try
        {
            var destination = Path.Combine(directory.FullName, "fresh.bin");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DaemonRelayService.ReceiveIntoAsync(
                destination,
                overwrite: false,
                Guid.NewGuid(),
                ChunksThenFault(new OperationCanceledException(), [1, 2, 3]),
                NullLogger.Instance,
                CancellationToken.None));

            Assert.Empty(Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ACompletedTransferReplacesTheDestinationAndSweepsItsTempFile()
    {
        var directory = Directory.CreateTempSubdirectory("ac-pull-done-");
        try
        {
            var destination = Path.Combine(directory.FullName, "target.bin");
            await File.WriteAllTextAsync(
                destination, "previous version", TestContext.Current.CancellationToken);

            await DaemonRelayService.ReceiveIntoAsync(
                destination,
                overwrite: true,
                Guid.NewGuid(),
                ChunksThenFault(fault: null, [1, 2], [3]),
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(
                new byte[] { 1, 2, 3 },
                await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal([destination], Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithoutOverwriteConsentAnExistingDestinationSurvivesACompletedTransfer()
    {
        // The temp-file detour must not lose the "do not clobber" guarantee that FileMode.CreateNew
        // used to enforce on the destination itself: the final move refuses without consent.
        var directory = Directory.CreateTempSubdirectory("ac-pull-noclobber-");
        try
        {
            var destination = Path.Combine(directory.FullName, "occupied.bin");
            await File.WriteAllTextAsync(
                destination, "already here", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() => DaemonRelayService.ReceiveIntoAsync(
                destination,
                overwrite: false,
                Guid.NewGuid(),
                ChunksThenFault(fault: null, [1, 2, 3]),
                NullLogger.Instance,
                CancellationToken.None));

            Assert.Equal(
                "already here",
                await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal([destination], Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async IAsyncEnumerable<byte[]> ChunksThenFault(Exception? fault, params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }

        if (fault is not null)
        {
            throw fault;
        }
    }
}
