using AngstromCommander.Daemon.FileOperations;
using AngstromCommander.Daemon.Identity;
using AngstromCommander.Daemon.Relay;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton(static sp =>
    new PathSandbox(sp.GetRequiredService<IOptions<RelayOptions>>().Value.AllowedRoots));
builder.Services.AddSingleton<ListDirectoryHandler>();
builder.Services.AddSingleton<DaemonIdentityStore>();
builder.Services.AddHttpClient<ServerApiClient>(static (sp, client) =>
{
    var serverUrl = sp.GetRequiredService<IOptions<RelayOptions>>().Value.ServerUrl;
    if (serverUrl is not null)
    {
        client.BaseAddress = serverUrl;
    }
});
builder.Services.AddHostedService<DaemonRelayService>();

var app = builder.Build();

app.MapGet("/healthz", () => "ok");

app.Run();

// Makes the implicit entry-point class nameable by WebApplicationFactory-based tests
// (which see it via InternalsVisibleTo in the csproj).
internal partial class Program;
