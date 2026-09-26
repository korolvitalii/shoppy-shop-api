using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using ShoppyShop.Api;
using ShoppyShop.Application;

using Testcontainers.PostgreSql;

namespace ShoppyShop.IntegrationTests;

public sealed class EdgeClientAddressTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ApiFactory? factory;
    private HttpClient? client;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString());
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    public void Dispose()
    {
        client?.Dispose();
        factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task VisitorsBehindTheEdgeEachGetTheirOwnSignInBudget()
    {
        // The production failure this exists for: every browser arrives through the same proxy, so
        // one shared "auth" budget meant ten sign-in attempts anywhere locked out everyone else.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("203.0.113.10", ApiFactory.EdgeSecret));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync("203.0.113.10", ApiFactory.EdgeSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync("203.0.113.11", ApiFactory.EdgeSecret));
    }

    [Fact]
    public async Task ForgedClientAddressesWithoutTheSecretShareOneBudget()
    {
        // Anyone can call the Railway host directly and set the header. Without the secret a
        // rotating forged address must buy nothing: every attempt lands in the same partition.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var forgedSecret = attempt % 2 == 0 ? null : "not-the-edge-secret-but-long-enough-to-look-real";
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync($"198.51.100.{attempt}", forgedSecret));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync("198.51.100.200", null));
    }

    [Fact]
    public async Task RefreshWithoutACookieIsNeverRateLimited()
    {
        // The storefront calls refresh on every page load, signed in or not. Those calls carry no
        // cookie and must not spend any budget, or ordinary browsing starves real sign-ins.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var response = await Client.PostAsync("/api/auth/refresh", null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task RefreshWithACookieIsRateLimitedPerClient()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync("203.0.113.20"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await RefreshAsync("203.0.113.20"));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync("203.0.113.21"));
    }

    private async Task<HttpStatusCode> LoginAsync(string clientIp, string? edgeSecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody@example.test", "Wrong!Password123"), options: JsonOptions),
        };
        AddEdgeHeaders(request, clientIp, edgeSecret);
        using var response = await Client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> RefreshAsync(string clientIp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.TryAddWithoutValidation("Cookie", "shoppy.refresh=not-a-real-token");
        AddEdgeHeaders(request, clientIp, ApiFactory.EdgeSecret);
        using var response = await Client.SendAsync(request);
        return response.StatusCode;
    }

    private static void AddEdgeHeaders(HttpRequestMessage request, string clientIp, string? edgeSecret)
    {
        request.Headers.TryAddWithoutValidation(EdgeClientAddress.ClientIpHeader, clientIp);
        if (edgeSecret is not null)
        {
            request.Headers.TryAddWithoutValidation(EdgeClientAddress.SecretHeader, edgeSecret);
        }
    }

    private HttpClient Client => client ?? throw new InvalidOperationException("Test client has not been initialized.");
}