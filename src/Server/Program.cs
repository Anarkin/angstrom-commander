using AngstromCommander.Server.Relay;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<IDaemonConnectionRegistry, InMemoryDaemonConnectionRegistry>();

var app = builder.Build();

app.MapGet("/healthz", () => "ok");
app.MapHub<DaemonHub>("/hub/daemon");
app.MapRelayEndpoints();

app.Run();

// Makes the implicit entry-point class nameable by WebApplicationFactory-based tests
// (which see it via InternalsVisibleTo in the csproj).
internal partial class Program;
