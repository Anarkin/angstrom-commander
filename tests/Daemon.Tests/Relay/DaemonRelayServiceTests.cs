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
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
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
}
