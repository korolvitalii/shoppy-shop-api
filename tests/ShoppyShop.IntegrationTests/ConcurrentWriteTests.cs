using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ShoppyShop.Application;
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