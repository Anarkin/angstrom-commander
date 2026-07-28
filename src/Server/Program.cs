var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/healthz", () => "ok");

app.Run();

// Makes the implicit entry-point class nameable by WebApplicationFactory-based tests
// (which see it via InternalsVisibleTo in the csproj).
internal partial class Program;
