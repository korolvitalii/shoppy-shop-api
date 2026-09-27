using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ShoppyShop.Domain;
using ShoppyShop.Infrastructure;

using Testcontainers.PostgreSql;

namespace ShoppyShop.IntegrationTests;

public sealed class ExpiredRecordCleanupTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ApiFactory? factory;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString());

        // Starting the host runs the initializer, which migrates the empty container.
        _ = factory.Services;
    }

    public async Task DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    public void Dispose()
    {
        factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SweepDeletesOnlyRecordsPastTheirExpiry()
    {
        var userId = Guid.NewGuid();
        var expiredOrder = CleanupOrder(userId);
        var liveOrder = CleanupOrder(userId);
        await using (var seed = CreateContext())
        {
            seed.RefreshSessions.AddRange(
                Session(userId, "expired-two-days-ago", Now.AddDays(-2)),
                Session(userId, "expired-within-retention", Now.AddHours(-12)),
                Session(userId, "live", Now.AddDays(5)),
                Session(userId, "revoked-not-expired", Now.AddDays(5), revokedAt: Now.AddDays(-1)));

            // One more than a batch, so the sweep has to go round again to finish.
            for (var index = 0; index <= ExpiredRecordCleanupService.BatchSize; index++)
            {
                seed.RefreshSessions.Add(Session(userId, $"backlog-{index}", Now.AddDays(-30)));
            }

            seed.Orders.AddRange(expiredOrder, liveOrder);
            seed.OrderRequests.AddRange(
                new OrderRequest { UserId = userId, Key = "expired", RequestHash = "hash", OrderId = expiredOrder.Id, ExpiresAt = Now.AddHours(-1) },
                new OrderRequest { UserId = userId, Key = "live", RequestHash = "hash", OrderId = liveOrder.Id, ExpiresAt = Now.AddHours(1) });
            await seed.SaveChangesAsync();
        }

        using var service = CreateService();
        var result = await service.SweepAsync(CancellationToken.None);

        Assert.Equal(ExpiredRecordCleanupService.BatchSize + 2, result.RefreshSessions);
        Assert.Equal(1, result.OrderRequests);

        await using var verify = CreateContext();
        Assert.Equal(
            ["expired-within-retention", "live", "revoked-not-expired"],
            await verify.RefreshSessions.AsNoTracking().OrderBy(x => x.TokenHash).Select(x => x.TokenHash).ToArrayAsync());
        Assert.Equal("live", Assert.Single(await verify.OrderRequests.AsNoTracking().ToArrayAsync()).Key);

        // The idempotency record goes; the order it pointed at is history and stays.
        Assert.Equal(2, await verify.Orders.CountAsync(x => x.UserId == userId));
    }

    [Fact]
    public async Task SweepYieldsWhileAnotherInstanceHoldsTheCleanupLock()
    {
        await using (var seed = CreateContext())
        {
            seed.RefreshSessions.Add(Session(Guid.NewGuid(), "expired", Now.AddDays(-2)));
            await seed.SaveChangesAsync();
        }

        using var service = CreateService();

        // Another replica mid-sweep: it holds the same transaction-scoped lock
        // ExpiredRecordCleanupService tries (its CleanupLockKey constant).
        await using (var otherInstance = CreateContext())
        {
            await using var transaction = await otherInstance.Database.BeginTransactionAsync();
            await otherInstance.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(2026092701)");

            var yielded = await service.SweepAsync(CancellationToken.None);
            Assert.Equal(new ExpiredRecordCleanupResult(0, 0), yielded);
        }

        var afterRelease = await service.SweepAsync(CancellationToken.None);
        Assert.Equal(1, afterRelease.RefreshSessions);
    }

    private ExpiredRecordCleanupService CreateService() => new(
        Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        new FixedTimeProvider(Now),
        Factory.Services.GetRequiredService<IConfiguration>(),
        NullLogger<ExpiredRecordCleanupService>.Instance);

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static RefreshSession Session(Guid userId, string tokenHash, DateTimeOffset expiresAt, DateTimeOffset? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = tokenHash,
        CreatedAt = expiresAt.AddDays(-7),
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
    };

    private static Order CleanupOrder(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CreatedAt = Now.AddDays(-1),
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

    private ApiFactory Factory => factory ?? throw new InvalidOperationException("Test factory has not been initialized.");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}