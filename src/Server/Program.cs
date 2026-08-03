using System.Threading.RateLimiting;
using AngstromCommander.Server.Auth;
using AngstromCommander.Server.Data;
using AngstromCommander.Server.Enrollment;
using AngstromCommander.Server.Relay;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Default receive limit is 32 KB; file chunks are 64 KB and arrive base64-inflated
// (~1.37x) over the JSON protocol, so give stream items generous headroom.
builder.Services.AddSignalR(static options => options.MaximumReceiveMessageSize = 512 * 1024);
builder.Services.AddMemoryCache();
builder.Services.AddOpenApi();

// Behind Azure Container Apps' ingress the connection the Server sees is the proxy's, so without
// this the caller address every rate-limit bucket keys on is the same one for everybody, and the
// scheme is http even though the client arrived over TLS.
builder.Services.Configure<ForwardedHeadersOptions>(static options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // The ingress address is assigned by the platform, so the default loopback-only allow-list
    // would discard the headers. Nothing reaches the Server except through that ingress.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// The WebClient is served from its own origin (app.<domain> against api.<domain> in a real
// environment, a different port in local dev), so it is cross-origin everywhere.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (builder.Environment.IsDevelopment())
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        // Named explicitly, and empty by default: an environment that has not been told its
        // WebClient origin refuses browsers rather than opening up to any of them.
        policy.WithOrigins(allowedOrigins);
    }

    // Tokens travel in the Authorization header, so no cookies and no credentials are involved.
    // Content-Disposition has to be exposed or the browser hides the downloaded file's name.
    policy.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
}));
builder.Services.AddSingleton<IDaemonConnectionRegistry, InMemoryDaemonConnectionRegistry>();
builder.Services.AddScoped<DaemonRelayOperations>();
builder.Services.AddHttpContextAccessor();

// The MCP endpoint: the same file-op surface as REST, spoken over Streamable HTTP for
// AI clients. Stateless — every request self-contained, nothing per-session in memory.
builder.Services.AddMcpServer()
    .WithHttpTransport(static options => options.Stateless = true)
    .WithTools<AngstromCommander.Server.Mcp.FileTools>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TransferLimitOptions>(builder.Configuration.GetSection(TransferLimitOptions.SectionName));
builder.Services.AddSingleton<FileTransferRegistry>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
// SignInManager comes along because it is what counts failed sign-in attempts and locks the
// account; the defaults are five tries, then five minutes out.
builder.Services.AddIdentityCore<AppUser>(static options => options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager();

var rateLimits = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
    ?? new RateLimitOptions();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        RateLimitPolicies.Authentication,
        http => PartitionByCaller(http, rateLimits.AuthenticationPermitsPerMinute));
    options.AddPolicy(
        RateLimitPolicies.EnrollmentPolling,
        http => PartitionByCaller(http, rateLimits.EnrollmentPollPermitsPerMinute));
});

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<RegistrationOptions>(builder.Configuration.GetSection(RegistrationOptions.SectionName));
builder.Services.AddSingleton<TokenService>();

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

// Two credential families share the Authorization header: JWTs (users, Daemons) and
// personal access tokens. The router picks the scheme by the PAT prefix, so each
// handler only ever sees its own kind.
builder.Services.AddAuthentication("TokenRouter")
    .AddPolicyScheme("TokenRouter", "JWT or PAT", static options =>
        options.ForwardDefaultSelector = static context =>
            context.Request.Headers.Authorization.ToString()
                .StartsWith($"Bearer {PersonalAccessTokens.Prefix}", StringComparison.Ordinal)
                ? PatAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, PatAuthenticationHandler>(
        PatAuthenticationHandler.SchemeName, displayName: null, configureOptions: null)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = TokenService.CreateSigningKey(authOptions.JwtSigningKey),
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
    .AddPolicy(AuthPolicies.Daemon, static policy => policy.RequireClaim(AuthClaims.TokenType, AuthClaims.DaemonTokenType))
    .AddPolicy(AuthPolicies.Mcp, static policy => policy
        .AddAuthenticationSchemes(PatAuthenticationHandler.SchemeName)
        .RequireClaim(AuthClaims.TokenType, AuthClaims.PatTokenType)
        .RequireClaim(AuthClaims.Scopes, PersonalAccessTokens.FilesScope));

// Fresh environments self-initialize their schema (ARCHITECTURE.md § Tech stack).
builder.Services.AddHostedService<DatabaseMigrator>();

var app = builder.Build();

// First in the pipeline: everything downstream that cares who the caller is — the rate limiter
// above all — has to see the real address rather than the proxy's.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => "ok");
app.MapHub<DaemonHub>("/hub/daemon");
app.MapAuthEndpoints();
app.MapEnrollmentEndpoints();
app.MapDaemonAuthEndpoints();
app.MapPatEndpoints();
app.MapRelayEndpoints();
app.MapMcp("/mcp").RequireAuthorization(AuthPolicies.Mcp);

await app.RunAsync();

// One bucket per caller address, which UseForwardedHeaders has already resolved to the real
// client rather than the ingress in front of it.
static RateLimitPartition<string> PartitionByCaller(HttpContext http, int permitsPerMinute)
{
    return RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        });
}

// Makes the implicit entry-point class nameable by WebApplicationFactory-based tests
// (which see it via InternalsVisibleTo in the csproj).
internal partial class Program;
