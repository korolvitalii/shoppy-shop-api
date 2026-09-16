using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ShoppyShop.Application;
using ShoppyShop.Domain;
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
        var firstPage = await Client.GetFromJsonAsync<ProductPageDto>("/api/products", JsonOptions);

        Assert.NotNull(groups);
        Assert.NotNull(firstPage);
        Assert.Equal(6, groups.Length);

        // The seeded catalogue is 54 products against a default page of 24, so the listing is paged
        // and the client is expected to follow the cursor.
        Assert.Equal(24, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);

        var ids = await DrainProductsAsync("/api/products");

        Assert.Equal(54, ids.Count);
        Assert.Equal(54, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ProductPagesAreStableAcrossEverySupportedSortOrder()
    {
        await Factory.Services.InitializeDatabaseAsync(Factory.Services.GetRequiredService<IConfiguration>());

        foreach (var sort in new[] { "featured", "price-asc", "price-desc", "name" })
        {
            // Against PostgreSQL rather than the unit tests' SQLite: the seek predicate compares
            // strings with the database's own collation, which is not the same ordering SQLite uses,
            // so paging has to be shown to agree with ORDER BY on the engine that actually serves it.
            var whole = await Client.GetFromJsonAsync<ProductPageDto>(
                $"/api/products?sort={sort}&limit=100",
                JsonOptions);
            var paged = await DrainProductsAsync($"/api/products?sort={sort}&limit=7");

            Assert.NotNull(whole);
            Assert.Null(whole.NextCursor);
            Assert.Equal(whole.Items.Select(product => product.Id), paged);
        }
    }

    [Fact]
    public async Task ProductListingRejectsAnUnusableCursorOrPageSize()
    {
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.GetAsync("/api/products?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.GetAsync("/api/products?limit=5000")).StatusCode);
    }

    private async Task<IReadOnlyList<string>> DrainProductsAsync(string url)
    {
        var ids = new List<string>();
        string? cursor = null;

        // Bounded so a cursor that fails to advance fails an assertion instead of hanging the suite.
        for (var page = 0; page < 30; page++)
        {
            var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var next = cursor is null ? url : $"{url}{separator}cursor={Uri.EscapeDataString(cursor)}";
            var result = await Client.GetFromJsonAsync<ProductPageDto>(next, JsonOptions);

            Assert.NotNull(result);
            ids.AddRange(result.Items.Select(product => product.Id));
            cursor = result.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        return ids;
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
    public async Task ConcurrentRefreshOfTheSameTokenLeavesNoLiveSessionOnceReuseIsDetected()
    {
        // The interleaving this guards against — the loser's family-revoke landing before the
        // winner's replacement session is inserted — is timing-dependent against real Postgres, so a
        // single race would not reliably exercise it. The fix makes the outcome hold regardless of
        // timing, so it is run more than once; the iteration count is kept low because each one spends
        // four requests against the "auth" rate limiter's 10-per-minute budget.
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var email = $"race-{Guid.NewGuid():N}@example.test";
            var register = await Client.PostAsJsonAsync(
                "/api/auth/register",
                new RegisterRequest(email, "Strong!Password123", null),
                JsonOptions);
            register.EnsureSuccessStatusCode();
            var originalCookie = register.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("shoppy.refresh=", StringComparison.Ordinal))
                .Split(';')[0];

            // Two independent clients replay the same refresh token at once, the way a stolen token
            // racing the legitimate client's own use of it would.
            var responses = await Task.WhenAll(
                SendRefreshWithCookieAsync(originalCookie),
                SendRefreshWithCookieAsync(originalCookie));

            var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);

            var replacementCookie = winner.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("shoppy.refresh=", StringComparison.Ordinal))
                .Split(';')[0];

            // Reuse was detected on this token family, so the replacement the winner just received
            // must be dead too — detected reuse ends the whole chain, not just the replayed token.
            // Before the fix, this could still succeed depending on how the two requests interleaved.
            var followUp = await SendRefreshWithCookieAsync(replacementCookie);
            Assert.Equal(HttpStatusCode.Unauthorized, followUp.StatusCode);
        }
    }

    [Fact]
    public async Task ReplayingAnOlderTokenDuringAnInFlightRotationLeavesNoLiveSessionEither()
    {
        // A: issued at registration. B: A rotated into B (sequential, legitimate). Then, concurrently:
        // an attacker replays the already-rotated A while the legitimate client is rotating B into C.
        // A family-revoke triggered by replaying A only inspects rows that exist when it starts; a
        // per-row lock on B does not make it discover C if C is inserted afterward. Only a per-user
        // lock held for the whole decision, on both sides, forces one full rotation-or-revoke to
        // finish before the other can even read the family's state.
        var email = $"oldreplay-{Guid.NewGuid():N}@example.test";
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "Strong!Password123", null),
            JsonOptions);
        register.EnsureSuccessStatusCode();
        var cookieA = register.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("shoppy.refresh=", StringComparison.Ordinal))
            .Split(';')[0];

        var rotateAtoB = await SendRefreshWithCookieAsync(cookieA);
        rotateAtoB.EnsureSuccessStatusCode();
        var cookieB = rotateAtoB.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("shoppy.refresh=", StringComparison.Ordinal))
            .Split(';')[0];

        var responses = await Task.WhenAll(SendRefreshWithCookieAsync(cookieB), SendRefreshWithCookieAsync(cookieA));
        var rotateBtoC = responses[0];
        var replayA = responses[1];

        // A was already revoked by the sequential A-to-B rotation above, so replaying it is reuse
        // regardless of how it interleaves with the B-to-C attempt.
        Assert.Equal(HttpStatusCode.Unauthorized, replayA.StatusCode);

        if (rotateBtoC.StatusCode == HttpStatusCode.OK)
        {
            var cookieC = rotateBtoC.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("shoppy.refresh=", StringComparison.Ordinal))
                .Split(';')[0];

            // Reuse was detected somewhere in this family, so C — even though it was actually
            // issued — must not be usable either. Before the per-user lock, C could survive.
            var followUp = await SendRefreshWithCookieAsync(cookieC);
            Assert.Equal(HttpStatusCode.Unauthorized, followUp.StatusCode);
        }
    }

    [Fact]
    public async Task PerUserAdvisoryLockForcesAFamilyRevokeToWaitForAConcurrentRotationsCommit()
    {
        // The interleaving above is real but not reliably forceable through two Task.WhenAll HTTP
        // requests — this drives it deterministically at the database level instead, against the
        // same connection AuthService uses, calling the exact primitive AuthService.
        // AcquireUserSessionLockAsync relies on (pg_advisory_xact_lock keyed by the user id).
        //
        // A bulk UPDATE's row set is fixed by its statement-start snapshot: waiting for a lock on a
        // row that snapshot already found only lets it re-check that row's latest value, it does not
        // expand the row set to include a row a still-open transaction inserts afterward. So this
        // holds a "rotation" transaction open across both the claim and the replacement insert, and
        // only lets a "revoke" transaction attempt its lock (and therefore its scan) while the
        // rotation is still uncommitted. If the lock did not serialize them, the revoke could commit
        // using a snapshot taken before the replacement existed.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        var userId = Guid.NewGuid();
        var sessionBId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        var lockKey = BitConverter.ToInt64(userId.ToByteArray(), 0);

        await using (var seed = new AppDbContext(options))
        {
            seed.RefreshSessions.Add(new RefreshSession
            {
                Id = sessionBId,
                UserId = userId,
                TokenHash = $"seed-b-{sessionBId:N}",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await seed.SaveChangesAsync();
        }

        var rotationHasClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revokeIsAboutToBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var rotation = Task.Run(async () =>
        {
            await using var rotationContext = new AppDbContext(options);
            await using var transaction = await rotationContext.Database.BeginTransactionAsync();
            await rotationContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})");

            var session = await rotationContext.RefreshSessions.SingleAsync(x => x.Id == sessionBId);
            session.RevokedAt = DateTimeOffset.UtcNow;
            session.ReplacedById = replacementId;
            await rotationContext.SaveChangesAsync();
            rotationHasClaimed.SetResult();

            // Give the revoke side time to actually issue its (blocking) lock request before this
            // transaction inserts the replacement and commits.
            await revokeIsAboutToBlock.Task;
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            rotationContext.RefreshSessions.Add(new RefreshSession
            {
                Id = replacementId,
                UserId = userId,
                TokenHash = $"seed-c-{replacementId:N}",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await rotationContext.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        await rotationHasClaimed.Task;

        var revoke = Task.Run(async () =>
        {
            await using var revokeContext = new AppDbContext(options);
            await using var transaction = await revokeContext.Database.BeginTransactionAsync();
            var lockRequest = revokeContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})");
            revokeIsAboutToBlock.SetResult();
            await lockRequest;

            await revokeContext.RefreshSessions
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        });

        await Task.WhenAll(rotation, revoke);

        await using var verify = new AppDbContext(options);
        var replacement = await verify.RefreshSessions.AsNoTracking().SingleAsync(x => x.Id == replacementId);
        Assert.NotNull(replacement.RevokedAt);
    }

    private async Task<HttpResponseMessage> SendRefreshWithCookieAsync(string cookie)
    {
        using var raceClient = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await raceClient.SendAsync(request);
    }

    [Fact]
    public async Task BootstrapAdminFailsClosedWhenTheConfiguredEmailAlreadyBelongsToANonAdminAccount()
    {
        // A customer self-registers with the address the operator later configures as
        // BootstrapAdmin:Email — accidentally, or by guessing a predictable value. Re-running
        // seeding against that configuration must refuse to promote this account rather than
        // silently handing it Admin without ever checking BootstrapAdmin:Password against it.
        var email = $"escalation-{Guid.NewGuid():N}@example.test";
        var password = "Strong!Password123";
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, password, null),
            JsonOptions);
        register.EnsureSuccessStatusCode();

        var bootstrapConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Email"] = email,
                ["BootstrapAdmin:Password"] = "Some!OtherStrongPassword456",
            })
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Factory.Services.InitializeDatabaseAsync(bootstrapConfig));
        Assert.Contains(email, error.Message, StringComparison.Ordinal);

        // Fail closed means no partial promotion, not just a thrown exception: the account must
        // still have no admin access afterward.
        var login = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), JsonOptions);
        login.EnsureSuccessStatusCode();
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        using var probeClient = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        probeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await probeClient.GetAsync("/api/admin/products")).StatusCode);
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