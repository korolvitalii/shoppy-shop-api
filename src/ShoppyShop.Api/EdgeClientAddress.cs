using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Primitives;

namespace ShoppyShop.Api;

/// <summary>
/// Restores the visitor's address from the header the Vercel edge adds to every proxied
/// <c>/api</c> request - believed only when the same request also carries the shared edge secret.
/// </summary>
/// <remarks>
/// <para>
/// Browsers reach this API through Vercel's <c>/api</c> rewrite and then Railway's edge, so the
/// connection's peer is Railway's proxy for every caller, and every per-IP rate-limit partition
/// collapses into one bucket. Trusting forwarded headers by network range cannot fix that here: past
/// Railway's hop the best it yields is Vercel's egress address, which is unpublished and shared, and
/// the Railway host is publicly reachable, so whatever a range-based rule believes can also be sent
/// straight to it by anyone.
/// </para>
/// <para>
/// The secret is what proves a request came through our Vercel project. Without a match - direct
/// callers, forged headers, or no secret configured yet - the address is left exactly as it was, so
/// the failure mode is the old shared bucket, never a failed start. A missing proxy setting that
/// refused to start once failed the Railway healthcheck (see README "Proxy trust boundary").
/// </para>
/// </remarks>
public static partial class EdgeClientAddress
{
    public const string ClientIpHeader = "X-Shoppy-Client-Ip";
    public const string SecretHeader = "X-Shoppy-Edge-Secret";
    public const int MinimumSecretLength = 32;

    private const string SecretSetting = "Proxy:EdgeSecret";

    public static bool IsConfigured(IConfiguration configuration) =>
        configuration[SecretSetting] is { Length: >= MinimumSecretLength };

    public static WebApplication UseEdgeClientAddress(this WebApplication app)
    {
        byte[]? expectedSecret = null;
        if (IsConfigured(app.Configuration))
        {
            expectedSecret = Encoding.UTF8.GetBytes(app.Configuration[SecretSetting]!);
        }
        else if (!string.IsNullOrEmpty(app.Configuration[SecretSetting]))
        {
            LogSecretTooShort(app.Logger, MinimumSecretLength);
        }

        var logPeerAddress = app.Configuration.GetValue("Diagnostics:LogPeerAddress", false);

        app.Use(async (context, next) =>
        {
            var headers = context.Request.Headers;
            var peer = context.Connection.RemoteIpAddress;

            IPAddress? client = null;
            var validated = expectedSecret is not null
                && Matches(headers[SecretHeader], expectedSecret)
                && TryParseSingle(headers[ClientIpHeader], out client);
            if (validated)
            {
                context.Connection.RemoteIpAddress = client;
            }

            // Whether or not they were believed, these never go further than this middleware.
            headers.Remove(SecretHeader);
            headers.Remove(ClientIpHeader);

            if (logPeerAddress)
            {
                LogPeerAddress(
                    app.Logger,
                    peer?.ToString(),
                    context.Connection.RemoteIpAddress?.ToString(),
                    headers["X-Forwarded-For"].ToString(),
                    headers["X-Real-IP"].ToString(),
                    validated,
                    ClientPartitionKey.For(context),
                    context.Request.Path.Value);
            }

            await next(context);
        });

        return app;
    }

    private static bool Matches(StringValues presented, byte[] expected) =>
        presented.Count == 1
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented[0] ?? string.Empty), expected);

    private static bool TryParseSingle(StringValues value, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        return value.Count == 1 && IPAddress.TryParse(value[0], out address);
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Proxy:EdgeSecret is shorter than {MinimumLength} characters and is ignored; the edge client address is not trusted.")]
    private static partial void LogSecretTooShort(ILogger logger, int minimumLength);

    // Temporary: Diagnostics:LogPeerAddress exists to verify the edge header end to end after deploy.
    // The secret itself is never logged, only whether it matched.
    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "PeerDiagnostic peer={Peer} client={Client} xff={ForwardedFor} realIp={RealIp} edgeValidated={EdgeValidated} partition={Partition} path={Path}")]
    private static partial void LogPeerAddress(
        ILogger logger,
        string? peer,
        string? client,
        string forwardedFor,
        string realIp,
        bool edgeValidated,
        string partition,
        string? path);
}