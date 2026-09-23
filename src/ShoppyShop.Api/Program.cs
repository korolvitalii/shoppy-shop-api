using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Scalar.AspNetCore;

using ShoppyShop.Api;
using ShoppyShop.Application;
using ShoppyShop.Infrastructure;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    var healthPort = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    using var healthResponse = await healthClient.GetAsync($"http://localhost:{healthPort}/health/live");
    return healthResponse.IsSuccessStatusCode ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);
var platformPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(platformPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{platformPort}");
}
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT configuration is required.");
if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
{
    throw new InvalidOperationException("JWT signing key must contain at least 32 bytes.");
}

builder.Services.AddProblemDetails();
// Runs the DataAnnotations on the request records in ShoppyShop.Application before an endpoint sees
// them, so malformed input is a 400 with a field map rather than whatever the first service to
// dereference it happens to throw.
builder.Services.AddValidation();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = "email",
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0)
    {
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
}));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardLimit = builder.Configuration.GetValue("Proxy:ForwardLimit", 1);

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    var trustedNetworks = builder.Configuration.GetSection("Proxy:TrustedNetworks").Get<string[]>() ?? [];
    foreach (var network in trustedNetworks)
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }

    // The scheme travels with the address or not at all: UseHttpsRedirection below reads
    // Request.Scheme, and behind a proxy that terminates TLS and forwards plain HTTP that is "http"
    // for every request unless X-Forwarded-Proto is honoured. Trusting one header and not the other
    // leaves the redirect middleware working from a scheme it can never see corrected.
    options.ForwardedHeaders = trustedNetworks.Length == 0
        ? ForwardedHeaders.None
        : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Partitioned by account rather than IP: these routes require authentication, and the abuse
    // being bounded is one account inflating its own collection. This is why UseRateLimiter runs
    // after UseAuthentication below — HttpContext.User is empty before it.
    options.AddPolicy("favorites", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("assistant", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
        }));
    // The catalogue is the only unauthenticated surface where the caller sizes the server's work
    // (see the search bounds in CatalogueService), so it needs a ceiling of its own. Set generously:
    // a storefront session legitimately fires a burst of these while a shopper filters and pages.
    options.AddPolicy("catalogue", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Partitioned by account, like "favorites": change-password verifies a password, so it wants
    // the same abuse ceiling as login, but it is authenticated and the subject being protected is
    // one account rather than one address.
    options.AddPolicy("password", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
var app = builder.Build();

// With no trusted network the forwarded headers are ignored (above), so behind a proxy
// RemoteIpAddress is the ingress for every caller and each per-IP partition collapses into one
// global bucket: "auth" becomes 10 requests per minute for the whole world, "assistant" 20 per hour
// and "catalogue" 120 per minute. This used to refuse to start, but the ingress range has not been
// measured yet (see README "Proxy trust boundary"), so it warns loudly instead of blocking deploys.
if (app.Environment.IsProduction()
    && app.Configuration.GetSection("Proxy:TrustedNetworks").Get<string[]>() is not { Length: > 0 })
{
    var logProxyTrustUnset = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "ProxyTrustUnset"),
        "Proxy:TrustedNetworks is not set in Production. Every request appears to originate from " +
        "the ingress, so the per-IP rate limits share a single global bucket.");
    logProxyTrustUnset(app.Logger, null);
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// After authentication, so a user-partitioned policy sees a populated HttpContext.User. The
// IP-partitioned policies behave identically either side of the move, and endpoint metadata is
// already available here, so RequireRateLimiting still applies before the endpoint runs.
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" })).ExcludeFromDescription();
app.MapGet("/health/ready", async (AppDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "healthy" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable"))
    .ExcludeFromDescription();
app.MapApiEndpoints();

// Always run. The initializer migrates *and* seeds, and the Customer role it creates is a hard
// prerequisite for registration - gating the whole call on AutoMigrate meant the documented
// "apply migrations yourself" route produced a schema with no roles, where the first registration
// threw after having already committed the user row. AutoMigrate now gates only the migration.
await app.Services.InitializeDatabaseAsync(builder.Configuration);

app.Run();

return 0;

public partial class Program;