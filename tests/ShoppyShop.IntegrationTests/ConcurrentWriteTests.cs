using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ShoppyShop.Application;
using ShoppyShop.Domain;
using ShoppyShop.Infrastructure;

using Testcontainers.PostgreSql;

namespace ShoppyShop.IntegrationTests;

/// <summary>
/// Races that two HTTP requests fired together cannot force reliably. Each test holds the advisory
/// lock the service under test is meant to take, from a separate connection. It waits until
/// Postgres reports the service blocked on that lock and only then releases it, so the order is
/// fixed. A service that does not take the lock never blocks, and the wait fails the test.
/// </summary>
public sealed class ConcurrentWriteTests : IAsyncLifetime, IDisposable
{
    private const string GroupId = "race-group";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ApiFactory? factory;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        factory = new ApiFactory(postgres.GetConnectionString());

        // Starting the host runs the initializer, which migrates and seeds the empty container.
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogoutAndRefreshOfTheSameTokenTakeTurnsAndLeaveNoLiveSession(bool refreshFirst)
    {
        AuthResult registered;
        using (var scope = Factory.Services.CreateScope())
        {
            registered = await scope.ServiceProvider.GetRequiredService<IAuthService>().RegisterAsync(
                new RegisterRequest($"logout-race-{Guid.NewGuid():N}@example.test", "Strong!Password123", null),
                CancellationToken.None);
        }

        var userId = registered.User.Id;

        // The key AuthService.AcquireUserSessionLockAsync derives from the user id.
        var userLockKey = BitConverter.ToInt64(userId.ToByteArray(), 0);
        Task<bool> refresh;
        Task logout;
        await using (var holder = CreateContext())
        {
            await using var transaction = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({userLockKey})");

            // Postgres grants waiters on one advisory lock in the order they queued.
            if (refreshFirst)
            {
                refresh = TryRefreshAsync(registered.RefreshToken);
                await WaitForBlockedLockRequestsAsync(1);
                logout = LogoutAsync(registered.RefreshToken);
            }
            else
            {
                logout = LogoutAsync(registered.RefreshToken);
                await WaitForBlockedLockRequestsAsync(1);
                refresh = TryRefreshAsync(registered.RefreshToken);
            }

            await WaitForBlockedLockRequestsAsync(2);
            await transaction.CommitAsync();
        }

        var refreshed = await refresh;
        await logout;

        // Refresh first: it rotates the token, and logout, finding it rotated, ends the successor.
        // Logout first: the token is revoked with no successor, and refresh is a plain 401.
        Assert.Equal(refreshFirst, refreshed);
        await using var verify = CreateContext();
        Assert.False(await verify.RefreshSessions.AnyAsync(x => x.UserId == userId && x.RevokedAt == null));
    }

    [Fact]
    public async Task ProductSaveQueuedBehindACategoryDeleteIsRejectedOnceTheDeleteCommits()
    {
        await SeedGroupAsync();

        Task<ProductDto> save;
        await using (var deletion = CreateContext())
        {
            // DeleteGroupAsync part-way through: category lock held, category and products swept,
            // not yet committed.
            await using var transaction = await deletion.Database.BeginTransactionAsync();
            await LockGroupAsync(deletion);
            await deletion.ProductGroups.Where(x => x.Id == GroupId)
                .ExecuteUpdateAsync(x => x.SetProperty(g => g.IsDeleted, true));
            await deletion.Products.IgnoreQueryFilters().Where(x => x.GroupId == GroupId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDeleted, true));

            save = SaveProductAsync("race-product");
            await WaitForBlockedLockRequestsAsync(1);
            await transaction.CommitAsync();
        }

        // Before the lock, the save checked the category before the delete committed, then inserted
        // a live product into a deleted category.
        await Assert.ThrowsAsync<AppUnprocessableException>(() => save);
        await using var verify = CreateContext();
        Assert.False(await verify.Products.IgnoreQueryFilters().AnyAsync(x => x.Id == "race-product" && !x.IsDeleted));
    }

    [Fact]
    public async Task CategoryDeleteQueuedBehindAProductSaveSweepsTheSavedProduct()
    {
        await SeedGroupAsync();

        Task deletion;
        await using (var save = CreateContext())
        {
            // UpsertProductAsync part-way through: category lock held, product inserted, not yet
            // committed.
            await using var transaction = await save.Database.BeginTransactionAsync();
            await LockGroupAsync(save);
            save.Products.Add(new Product
            {
                Id = "in-flight-product",
                GroupId = GroupId,
                Name = "In-flight product",
                Brand = "Brand",
                Description = "Description",
                ImageUrl = "https://example.test/product.jpg",
                Price = 10,
                InStock = true,
            });
            await save.SaveChangesAsync();

            deletion = DeleteGroupAsync();
            await WaitForBlockedLockRequestsAsync(1);
            await transaction.CommitAsync();
        }

        await deletion;

        // Before the lock, the delete's sweep could not see the uncommitted product, finished first,
        // and the product then committed live under a deleted category.
        await using var verify = CreateContext();
        var product = await verify.Products.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == "in-flight-product");
        Assert.True(product.IsDeleted);
    }

    private Task<bool> TryRefreshAsync(string refreshToken) => Task.Run(async () =>
    {
        using var scope = Factory.Services.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IAuthService>().RefreshAsync(refreshToken, CancellationToken.None);
            return true;
        }
        catch (AppUnauthorizedException)
        {
            return false;
        }
    });

    private Task LogoutAsync(string refreshToken) => Task.Run(async () =>
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAuthService>().LogoutAsync(refreshToken, CancellationToken.None);
    });

    private Task<ProductDto> SaveProductAsync(string productId) => Task.Run(async () =>
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminCatalogueService>().UpsertProductAsync(
            productId,
            new ProductWriteRequest(productId, GroupId, "Race product", "Brand", "Description", "https://example.test/product.jpg", 10, null, true),
            CancellationToken.None);
    });

    private Task DeleteGroupAsync() => Task.Run(async () =>
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAdminCatalogueService>().DeleteGroupAsync(GroupId, CancellationToken.None);
    });

    // The key AdvisoryLocks takes: its lock space (CatalogueGroup = 1), then the id.
    private static Task<int> LockGroupAsync(AppDbContext dbContext) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(1, hashtext({GroupId}))");

    private async Task SeedGroupAsync()
    {
        await using var seed = CreateContext();
        seed.ProductGroups.Add(new ProductGroup
        {
            Id = GroupId,
            Name = "Race group",
            Description = "Description",
            ImageUrl = "https://example.test/group.jpg",
        });
        await seed.SaveChangesAsync();
    }

    private async Task WaitForBlockedLockRequestsAsync(int count)
    {
        await using var probe = CreateContext();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (await probe.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted")
            .SingleAsync() < count)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} blocked advisory lock request(s); the operation under test never waited for the lock.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private ApiFactory Factory => factory ?? throw new InvalidOperationException("Test factory has not been initialized.");
}