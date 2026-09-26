using System.Globalization;
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
// Parsed here, not inside the options callback, so a mistyped value is skipped and reported (after
// the app is built, below) instead of thrown: a throw while the pipeline is built fails Railway's
// healthcheck exactly the way the old Production refusal to start did.
var invalidProxySettings = new List<string>();
var trustedNetworks = new List<System.Net.IPNetwork>();
foreach (var entry in builder.Configuration.GetSection("Proxy:TrustedNetworks").GetChildren())
{
    if (System.Net.IPNetwork.TryParse(entry.Value?.Trim(), out var network))
    {
        trustedNetworks.Add(network);
    }
    else
    {
        invalidProxySettings.Add($"{entry.Path}='{entry.Value}'");
    }
}

var forwardLimit = 1;
if (builder.Configuration["Proxy:ForwardLimit"] is { } forwardLimitSetting)
{
    if (int.TryParse(forwardLimitSetting, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
    {
        forwardLimit = parsed;
    }
    else
    {
        invalidProxySettings.Add($"Proxy:ForwardLimit='{forwardLimitSetting}'");
    }
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardLimit = forwardLimit;

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var network in trustedNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }

    // The scheme travels with the address or not at all: UseHttpsRedirection below reads
    // Request.Scheme, and behind a proxy that terminates TLS and forwards plain HTTP that is "http"
    // for every request unless X-Forwarded-Proto is honoured. Trusting one header and not the other
    // leaves the redirect middleware working from a scheme it can never see corrected.
    options.ForwardedHeaders = trustedNetworks.Count == 0
        ? ForwardedHeaders.None
        : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartitionKey.For(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Split from "auth" because the storefront calls refresh on every page load to restore a
    // session, signed in or not, and sharing login's budget let ordinary browsing lock everyone out
    // of signing in. A request with no refresh cookie is rejected before any database work, so it
    // costs nothing worth limiting; with one, the budget is per client.
    options.AddPolicy("refresh", context => context.Request.Cookies.ContainsKey(ApiEndpoints.RefreshCookie)
        ? RateLimitPartition.GetFixedWindowLimiter(
            ClientPartitionKey.For(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })
        : RateLimitPartition.GetNoLimiter("no-refresh-cookie"));
    // Partitioned by account rather than IP: these routes require authentication, and the abuse
    // being bounded is one account inflating its own collection. This is why UseRateLimiter runs
    // after UseAuthentication below — HttpContext.User is empty before it.
    options.AddPolicy("favorites", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? ClientPartitionKey.For(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("assistant", context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartitionKey.For(context),
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
        ClientPartitionKey.For(context),
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
        context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? ClientPartitionKey.For(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
var app = builder.Build();

if (invalidProxySettings.Count > 0)
{
    var logInvalidProxySettings = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2, "InvalidProxySettings"),
        "Ignoring invalid proxy settings: {Settings}");
    logInvalidProxySettings(app.Logger, string.Join(", ", invalidProxySettings), null);
}

// With neither the edge secret (EdgeClientAddress) nor a trusted network, RemoteIpAddress behind a
// proxy is the ingress for every caller and each per-IP partition collapses into one global bucket:
// "auth" becomes 10 requests per minute for the whole world, "assistant" 20 per hour and
// "catalogue" 120 per minute. Warn, never refuse: refusing to start failed the Railway healthcheck
// on the PR #46 deploy, and the shared bucket is the lesser outage.
if (app.Environment.IsProduction()
    && trustedNetworks.Count == 0
    && !EdgeClientAddress.IsConfigured(app.Configuration))
{
    var logProxyTrustUnset = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "ProxyTrustUnset"),
        "Neither Proxy:EdgeSecret nor Proxy:TrustedNetworks is set in Production. Every request " +
        "appears to originate from the ingress, so the per-IP rate limits share a single global bucket.");
    logProxyTrustUnset(app.Logger, null);
}

app.UseForwardedHeaders();
app.UseEdgeClientAddress();
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