using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task ProductListingFiltersByNewAndGiftWrappableAndReportsTotalCountOnceOnly()
    {
        await Factory.Services.InitializeDatabaseAsync(Factory.Services.GetRequiredService<IConfiguration>());

        // The frontend seed's isNew/giftWrappable values come from a deterministic 1-in-6 / 1-in-3
        // spread over the product list, so both filters are guaranteed to select a non-empty,
        // non-total subset of the 54 seeded products.
        var newOnly = await Client.GetFromJsonAsync<ProductPageDto>("/api/products?isNew=true&limit=100", JsonOptions);
        Assert.NotNull(newOnly);
        Assert.All(newOnly.Items, product => Assert.True(product.IsNew));
        Assert.NotEmpty(newOnly.Items);
        Assert.NotEqual(54, newOnly.Items.Count);

        var giftWrappableOnly = await Client.GetFromJsonAsync<ProductPageDto>("/api/products?giftWrappable=true&limit=100", JsonOptions);
        Assert.NotNull(giftWrappableOnly);
        Assert.All(giftWrappableOnly.Items, product => Assert.True(product.GiftWrappable));
        Assert.NotEmpty(giftWrappableOnly.Items);

        // Total count is only worth its cost once per filter change: present on the first page,
        // absent once a cursor says this is a later page of the same listing.
        var firstPage = await Client.GetFromJsonAsync<ProductPageDto>("/api/products", JsonOptions);
        Assert.NotNull(firstPage);
        Assert.Equal(54, firstPage.TotalCount);

        var secondPage = await Client.GetFromJsonAsync<ProductPageDto>(
            $"/api/products?cursor={Uri.EscapeDataString(firstPage.NextCursor!)}",
            JsonOptions);
        Assert.NotNull(secondPage);
        Assert.Null(secondPage.TotalCount);
    }

    [Fact]
    public async Task UpgradingAnExistingDatabaseBackfillsNewFlagsToMatchTheSeed()
    {
        // Simulates a database that already had rows before the flags migration existed: migrate up
        // to just before it, insert rows under the old schema, then let the flags migration run and
        // check its backfill against the same ids the catalogue.json seed assigns them — the two must
        // agree, or a database that upgrades in place disagrees with one seeded fresh from that file.
        //
        // Deliberately uses several ids spanning multiple groups and every isNew/giftWrappable
        // combination present in the seed, rather than a single row: a lone fixture cannot tell an
        // explicit id-keyed backfill apart from a row-order/row-count based one (e.g. the original
        // ROW_NUMBER() % 6 bug), because with only one row in the table almost any such formula
        // degenerates to the same output. Four ids across three groups makes that coincidence
        // vanishingly unlikely while still catching the exact regression this test was added for.
        //
        // This needs its own container rather than the class's shared one: building an ApiFactory
        // against a database — which every other test does — runs Program.cs's own startup
        // migrate-and-seed before the test body gets a chance to stop at a partial migration.
        await using var upgradePostgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await upgradePostgres.StartAsync();
        var connectionString = upgradePostgres.GetConnectionString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;

        await using (var dbContext = new AppDbContext(options))
        {
            var migrator = ((IInfrastructure<IServiceProvider>)dbContext.Database).Instance
                .GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("20260906183730_ProductPaginationIndexes");

            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "ProductGroups" ("Id","Name","Description","ImageUrl","DisplayOrder","IsDeleted")
                VALUES
                    ('beauty','Beauty','d','/g.jpg',0,false),
                    ('electronics','Electronics','d','/g.jpg',1,false),
                    ('home','Home','d','/g.jpg',2,false)
                """);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "Products" ("Id","GroupId","Name","Brand","Description","ImageUrl","Price","InStock","IsDeleted")
                VALUES
                    ('beauty-1','beauty','Pre-existing product','Brand','d','/p.jpg',10,true,false),
                    ('beauty-2','beauty','Pre-existing product','Brand','d','/p.jpg',10,true,false),
                    ('electronics-4','electronics','Pre-existing product','Brand','d','/p.jpg',10,true,false),
                    ('home-1','home','Pre-existing product','Brand','d','/p.jpg',10,true,false)
                """);
        }

        await using (var dbContext = new AppDbContext(options))
        {
            var migrator = ((IInfrastructure<IServiceProvider>)dbContext.Database).Instance
                .GetRequiredService<IMigrator>();
            await migrator.MigrateAsync();
        }

        await using (var dbContext = new AppDbContext(options))
        {
            var products = await dbContext.Products.IgnoreQueryFilters()
                .Where(x => new[] { "beauty-1", "beauty-2", "electronics-4", "home-1" }.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id);

            // Expected values taken from catalogue.json for each id.
            Assert.True(products["beauty-1"].IsNew);
            Assert.True(products["beauty-1"].GiftWrappable);

            Assert.False(products["beauty-2"].IsNew);
            Assert.False(products["beauty-2"].GiftWrappable);

            Assert.True(products["electronics-4"].IsNew);
            Assert.True(products["electronics-4"].GiftWrappable);

            Assert.False(products["home-1"].IsNew);
            Assert.True(products["home-1"].GiftWrappable);
        }
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

    [Fact]
    public async Task OrderHistoryIsCappedAndSeeksPastTheBeforeCutoff()
    {
        // Covered here rather than as a unit test: the window orders by CreatedAt, and SQLite - which
        // the unit tests run on - cannot ORDER BY a DateTimeOffset at all.
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("history@example.test", "Strong!Password123", null),
            JsonOptions);
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var scope = Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var index = 0; index < 60; index++)
            {
                dbContext.Orders.Add(HistoryOrder(auth.User.Id, start.AddMinutes(index)));
            }

            await dbContext.SaveChangesAsync();
        }

        // Unbounded, this returned all 60 with every line attached.
        var capped = await Client.GetFromJsonAsync<OrderDto[]>("/api/orders", JsonOptions);
        Assert.Equal(50, capped!.Length);
        Assert.Equal(start.AddMinutes(59), capped[0].CreatedAt);
        Assert.Equal(start.AddMinutes(10), capped[^1].CreatedAt);

        var limited = await Client.GetFromJsonAsync<OrderDto[]>("/api/orders?limit=5", JsonOptions);
        Assert.Equal(5, limited!.Length);

        // The cursor is the last item of the previous page - CreatedAt and id together, not
        // CreatedAt alone (see OrderHistorySeeksThroughOrdersSharingTheSameTimestamp for why).
        var lastOfFirstPage = capped[^1];
        var cutoff = Uri.EscapeDataString(lastOfFirstPage.CreatedAt.ToString("O"));
        var older = await Client.GetFromJsonAsync<OrderDto[]>(
            $"/api/orders?limit=3&before={cutoff}&beforeId={lastOfFirstPage.Id}", JsonOptions);
        Assert.Equal(
            [start.AddMinutes(9), start.AddMinutes(8), start.AddMinutes(7)],
            older!.Select(x => x.CreatedAt));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.GetAsync("/api/orders?limit=101")).StatusCode);

        // Half a cursor is rejected rather than silently falling back to the lossy single-column
        // comparison that used to drop orders sharing a timestamp with the boundary.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.GetAsync($"/api/orders?limit=3&before={cutoff}")).StatusCode);
    }

    [Fact]
    public async Task OrderHistorySeeksThroughOrdersSharingTheSameTimestamp()
    {
        // The reviewer's concrete failure case: two orders at the exact same CreatedAt and limit=1.
        // A cursor keyed on CreatedAt alone would skip the second one at the boundary - it must not.
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("history-tie@example.test", "Strong!Password123", null),
            JsonOptions);
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var tie = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        using (var scope = Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Orders.Add(HistoryOrder(auth.User.Id, tie));
            dbContext.Orders.Add(HistoryOrder(auth.User.Id, tie));
            await dbContext.SaveChangesAsync();
        }

        var first = await Client.GetFromJsonAsync<OrderDto[]>("/api/orders?limit=1", JsonOptions);
        Assert.Single(first!);
        Assert.Equal(tie, first![0].CreatedAt);

        var cursorBefore = Uri.EscapeDataString(first[0].CreatedAt.ToString("O"));
        var second = await Client.GetFromJsonAsync<OrderDto[]>(
            $"/api/orders?limit=1&before={cursorBefore}&beforeId={first[0].Id}", JsonOptions);

        Assert.Single(second!);
        Assert.Equal(tie, second![0].CreatedAt);
        Assert.NotEqual(first[0].Id, second[0].Id);
    }

    [Fact]
    public async Task PublicCatalogueRequestsAreRateLimited()
    {
        // The catalogue is the only unauthenticated endpoint whose per-request cost the caller sizes,
        // and it had no policy at all. TestServer reports no remote address, so every request here
        // lands in the same partition - which is exactly what needs exhausting.
        HttpStatusCode? limited = null;
        for (var attempt = 0; attempt < 130 && limited is null; attempt++)
        {
            using var response = await Client.GetAsync("/api/products?limit=1");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response.StatusCode;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, limited);
    }

    [Fact]
    public async Task OverlongCatalogueSearchIsRejectedRatherThanScannedPerWord()
    {
        var response = await Client.GetAsync($"/api/products?search={new string('a', 121)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IdentityRolesAreSeededEvenWhenAutomaticMigrationIsDisabled()
    {
        // The documented "apply migrations yourself" route: schema created out of band, API started
        // with Database:AutoMigrate=false. That flag used to gate the whole initializer, so the
        // Customer role was never created and the first registration threw during role assignment -
        // after having already committed the user row, leaving an account that blocked the retry.
        //
        // Needs its own container: the class's shared one is migrated and seeded by the factory the
        // other tests build, which would mask exactly what this asserts.
        await using var manualPostgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await manualPostgres.StartAsync();
        var connectionString = manualPostgres.GetConnectionString();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using (var dbContext = new AppDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        await using var manualFactory = new ApiFactory(connectionString, autoMigrate: false);
        using var manualClient = manualFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        var register = await manualClient.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("manual@example.test", "Strong!Password123", null),
            JsonOptions);

        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Assert.Contains("Customer", auth!.User.Roles);
    }

    private static Order HistoryOrder(Guid userId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CreatedAt = createdAt,
        Currency = "GBP",
        Status = "confirmed",
        DeliveryName = "Test Customer",
        DeliveryEmail = "customer@example.test",
        DeliveryAddressLine1 = "1 Test Street",
        DeliveryCity = "London",
        DeliveryPostcode = "SW1A 1AA",
        DeliveryCountry = "United Kingdom",
        DeliveryMethod = "standard",
        PaymentTokenId = "tok_test_only",
        PaymentBrand = "Visa",
        PaymentLast4 = "4242",
        Subtotal = 10m,
        DeliveryCharge = 4.99m,
        Total = 14.99m,
    };

    private HttpClient Client => client ?? throw new InvalidOperationException("Test client has not been initialized.");
    private ApiFactory Factory => factory ?? throw new InvalidOperationException("Test factory has not been initialized.");

    private sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);
}