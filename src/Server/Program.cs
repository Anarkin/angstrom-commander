using System.Text;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using AngstromCommander.Server.Enrollment;
using AngstromCommander.Server.Relay;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Default receive limit is 32 KB; file chunks are 64 KB and arrive base64-inflated
// (~1.37x) over the JSON protocol, so give stream items generous headroom.
builder.Services.AddSignalR(static options => options.MaximumReceiveMessageSize = 512 * 1024);
builder.Services.AddMemoryCache();
builder.Services.AddCors();
builder.Services.AddSingleton<IDaemonConnectionRegistry, InMemoryDaemonConnectionRegistry>();
builder.Services.AddSingleton<FileTransferRegistry>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddIdentityCore<AppUser>(static options => options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<TokenService>();

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.JwtSigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            // SignalR clients carry the bearer token in the query string on the connect request.
            OnMessageReceived = static context =>
            {
                var accessToken = context.Request.Query["access_token"].ToString();
                if (accessToken.Length > 0
                    && context.HttpContext.Request.Path.StartsWithSegments("/hub/daemon", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.User, static policy => policy.RequireClaim(AuthClaims.TokenType, AuthClaims.UserTokenType))
    .AddPolicy(AuthPolicies.Daemon, static policy => policy.RequireClaim(AuthClaims.TokenType, AuthClaims.DaemonTokenType));

var app = builder.Build();

// Fresh environments self-initialize their schema (ARCHITECTURE.md § Tech stack).
if (app.Configuration.GetValue("Database:MigrateOnStartup", defaultValue: true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    // Lets local dev pages (e.g. the future WebClient dev server) call the API from
    // another origin. Development only — never in real environments.
    app.UseCors(static policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => "ok");
app.MapHub<DaemonHub>("/hub/daemon");
app.MapAuthEndpoints();
app.MapEnrollmentEndpoints();
app.MapDaemonAuthEndpoints();
app.MapRelayEndpoints();

await app.RunAsync();

// Makes the implicit entry-point class nameable by WebApplicationFactory-based tests
// (which see it via InternalsVisibleTo in the csproj).
internal partial class Program;
