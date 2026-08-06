using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure;

using Testcontainers.PostgreSql;

namespace ShoppyShop.IntegrationTests;

public sealed class ApiFlowTests : IAsyncLifetime, IDisposable
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
            HandleCookies = true,
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
    public async Task PublicCatalogueUsesFrontendSeedData()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/ready")).StatusCode);
        var openApi = await Client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("\"Bearer\"", openApi, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/scalar/v1")).StatusCode);
        await Factory.Services.InitializeDatabaseAsync(Factory.Services.GetRequiredService<IConfiguration>());

        var groups = await Client.GetFromJsonAsync<ProductGroupDto[]>("/api/product-groups", JsonOptions);
        var products = await Client.GetFromJsonAsync<ProductDto[]>("/api/products", JsonOptions);

        Assert.NotNull(groups);
        Assert.NotNull(products);
        Assert.Equal(6, groups.Length);
        Assert.Equal(54, products.Length);
    }

    [Fact]
    public async Task FeatureConfigEndpointReturnsAppSettingsDefaults()
    {
        var config = await Client.GetFromJsonAsync<FeatureConfigDto>("/api/feature-config", JsonOptions);

        Assert.True(config!.AssistantEnabled);
    }

    [Fact]
    public async Task CustomerCanFavoriteAndCreateIdempotentOrder()
    {
        var email = $"customer-{Guid.NewGuid():N}@example.test";
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "Strong!Password123", "Test Customer"),
            JsonOptions);
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Assert.NotNull(auth);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var favorite = await Client.PutAsync("/api/favorites/beauty-1", null);
        Assert.Equal(HttpStatusCode.NoContent, favorite.StatusCode);
        var favorites = await Client.GetFromJsonAsync<ProductDto[]>("/api/favorites", JsonOptions);
        Assert.Single(favorites!);

        var request = new CreateOrderRequest(
            [new OrderItemRequest("beauty-1", "beauty", "Manipulated name", "/wrong.jpg", 0.01m, 2)],
            new DeliveryAddress("Test Customer", email, "1 Test Street", "London", "SW1A 1AA", "United Kingdom"),
            "standard",
            new PaymentSummary("tok_test_only", "Visa", "4242"),
            0.02m,
            0,
            0.02m);
        const string idempotencyKey = "integration-test-order-key";
        using var firstMessage = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        firstMessage.Headers.Add("Idempotency-Key", idempotencyKey);
        var first = await Client.SendAsync(firstMessage);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstOrder = await first.Content.ReadFromJsonAsync<OrderDto>(JsonOptions);

        using var retryMessage = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        retryMessage.Headers.Add("Idempotency-Key", idempotencyKey);
        var retry = await Client.SendAsync(retryMessage);
        var retriedOrder = await retry.Content.ReadFromJsonAsync<OrderDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(firstOrder!.Id, retriedOrder!.Id);
        Assert.StartsWith("ORD-", firstOrder.Id, StringComparison.Ordinal);
        Assert.Equal(248.93m, firstOrder.Total);
        Assert.Equal(121.97m, firstOrder.Lines.Single().UnitPrice);

        var changedRequest = request with
        {
            Lines = [new OrderItemRequest("beauty-1", "beauty", "Name", "/image.jpg", 1, 1)],
        };
        using var conflictMessage = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(changedRequest, options: JsonOptions),
        };
        conflictMessage.Headers.Add("Idempotency-Key", idempotencyKey);
        var conflict = await Client.SendAsync(conflictMessage);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var secondRegistration = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"second-{Guid.NewGuid():N}@example.test", "Strong!Password123", "Second Customer"),
            JsonOptions);
        var secondAuth = await secondRegistration.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondAuth!.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/orders/{firstOrder.Id}")).StatusCode);
        Assert.Empty((await Client.GetFromJsonAsync<ProductDto[]>("/api/favorites", JsonOptions))!);
    }

    [Fact]
    public async Task AuthenticationLifecycleRotatesAndRevokesRefreshTokens()
    {
        var email = $"auth-{Guid.NewGuid():N}@example.test";
        var password = "Strong!Password123";
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, password, "Authentication Test"),
            JsonOptions);
        register.EnsureSuccessStatusCode();
        var initial = await register.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        var originalCookie = register.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("shoppy.refresh=", StringComparison.Ordinal))
            .Split(';')[0];
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", initial!.AccessToken);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, password, null), JsonOptions)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Wrong!Password123"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync("/api/admin/products")).StatusCode);

        var refresh = await Client.PostAsync("/api/auth/refresh", null);
        refresh.EnsureSuccessStatusCode();
        var rotated = await refresh.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated!.AccessToken);

        using var replayClient = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        replayRequest.Headers.TryAddWithoutValidation("Cookie", originalCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replayClient.SendAsync(replayRequest)).StatusCode);

        var newPassword = "New!StrongPassword456";
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.PostAsJsonAsync(
                "/api/auth/change-password",
                new ChangePasswordRequest(password, newPassword),
                JsonOptions)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), JsonOptions)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, newPassword), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task AdministratorCanManageSoftDeletedCatalogue()
    {
        var login = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.test", "Admin!IntegrationPassword123"),
            JsonOptions);
        login.EnsureSuccessStatusCode();
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var group = new ProductGroupWriteRequest(
            "test-group",
            "Test group",
            "Test description",
            "https://example.test/group.jpg",
            "New",
            99);
        (await Client.PutAsJsonAsync("/api/admin/product-groups/test-group", group, JsonOptions)).EnsureSuccessStatusCode();
        var product = new ProductWriteRequest(
            "test-product",
            "test-group",
            "Test product",
            "Test brand",
            "Test description",
            "https://example.test/product.jpg",
            20,
            15,
            true);
        (await Client.PutAsJsonAsync("/api/admin/products/test-product", product, JsonOptions)).EnsureSuccessStatusCode();

        var groups = await Client.GetFromJsonAsync<ProductGroupDto[]>("/api/admin/product-groups", JsonOptions);
        Assert.Equal(1, groups!.Single(x => x.Id == "test-group").ItemCount);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync("/api/admin/products/test-product")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.GetAsync("/api/product-groups/test-group/products/test-product")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync("/api/admin/product-groups/test-group")).StatusCode);
    }

    private HttpClient Client => client ?? throw new InvalidOperationException("Test client has not been initialized.");
    private ApiFactory Factory => factory ?? throw new InvalidOperationException("Test factory has not been initialized.");

    private sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);
}