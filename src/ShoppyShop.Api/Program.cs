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
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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
});
var app = builder.Build();
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

if (builder.Configuration.GetValue("Database:AutoMigrate", true))
{
    await app.Services.InitializeDatabaseAsync(builder.Configuration);
}

app.Run();

return 0;

public partial class Program;