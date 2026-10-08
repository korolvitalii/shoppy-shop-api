using System.Data.Common;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure;

using Testcontainers.PostgreSql;

namespace ShoppyShop.IntegrationTests;

/// <summary>
/// A commit that lands in the database while the connection drops before its acknowledgement
/// reaches the API. The retrying execution strategy sees a transient error and runs the whole
/// operation again, against data the first attempt has already changed - EF Core's documented
/// "transaction commit failure" case. Real networks produce this rarely and never on demand, so an
/// interceptor lets the commit succeed and then throws the error a dropped connection would.
/// </summary>
public sealed class CommitAcknowledgementTests : IAsyncLifetime, IDisposable
{
    private const string Password = "Strong!Password123";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly LoseNextCommitAcknowledgement faults = new();
    private ApiFactory? baseFactory;
    private WebApplicationFactory<Program>? factory;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        baseFactory = new ApiFactory(postgres.GetConnectionString());
        factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(faults))));

        // Starting the host runs the initializer, which migrates and seeds the empty container.
        _ = factory.Services;
    }

    public async Task DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        if (baseFactory is not null)
        {
            await baseFactory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    public void Dispose()
    {
        factory?.Dispose();
        baseFactory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RefreshRetriedAfterItsOwnCommitReturnsTheSuccessorItCreated()
    {
        var email = $"commit-retry-{Guid.NewGuid():N}@example.test";
        var registered = await WithAuthAsync(auth => auth.RegisterAsync(new RegisterRequest(email, Password, null), CancellationToken.None));
        await WithAuthAsync(auth => auth.LoginAsync(new LoginRequest(email, Password), CancellationToken.None));

        faults.Arm();
        var refreshed = await WithAuthAsync(auth => auth.RefreshAsync(registered.RefreshToken, CancellationToken.None));

        // The first attempt did commit and then fail, so what follows is the retry's answer.
        Assert.True(faults.Fired);

        // The retry found the token rotated by the attempt before it. Taking that for reuse revoked
        // the whole family: the other device's session and this request's own successor with it.
        await using (var verify = CreateContext())
        {
            Assert.Equal(2, await verify.RefreshSessions.CountAsync(x => x.UserId == registered.User.Id && x.RevokedAt == null));
        }

        // The token handed back is the successor that was stored, not a token with no row behind it.
        var next = await WithAuthAsync(auth => auth.RefreshAsync(refreshed.RefreshToken, CancellationToken.None));
        Assert.NotEqual(refreshed.RefreshToken, next.RefreshToken);
    }

    [Fact]
    public async Task RefreshRetryDoesNotReviveASuccessorEndedBetweenTheAttempts()
    {
        var email = $"commit-retry-ended-{Guid.NewGuid():N}@example.test";
        var registered = await WithAuthAsync(auth => auth.RegisterAsync(new RegisterRequest(email, Password, null), CancellationToken.None));

        // The per-user lock is released between the attempts, so a password change on another
        // device can sweep the family - successor included - before the retry runs.
        faults.Arm(async () =>
        {
            await using var sweep = CreateContext();
            await sweep.RefreshSessions
                .Where(x => x.UserId == registered.User.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTimeOffset.UtcNow));
        });

        await Assert.ThrowsAsync<AppUnauthorizedException>(() =>
            WithAuthAsync(auth => auth.RefreshAsync(registered.RefreshToken, CancellationToken.None)));
        Assert.True(faults.Fired);

        await using var verify = CreateContext();
        Assert.False(await verify.RefreshSessions.AnyAsync(x => x.UserId == registered.User.Id && x.RevokedAt == null));
    }

    private async Task<T> WithAuthAsync<T>(Func<IAuthService, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAuthService>());
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private WebApplicationFactory<Program> Factory => factory ?? throw new InvalidOperationException("Test factory has not been initialized.");

    /// <summary>
    /// Once armed, lets the next commit succeed and then fails it with the transient error a dropped
    /// connection produces, which is what the retrying execution strategy re-runs on.
    /// </summary>
    private sealed class LoseNextCommitAcknowledgement : DbTransactionInterceptor
    {
        private Func<Task>? afterCommit;

        public bool Fired { get; private set; }

        public void Arm(Func<Task>? betweenAttempts = null) => afterCommit = betweenAttempts ?? (() => Task.CompletedTask);

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref afterCommit, null) is not { } betweenAttempts)
            {
                return;
            }

            Fired = true;
            await betweenAttempts();
            throw new NpgsqlException("Simulated loss of the commit acknowledgement.", new TimeoutException());
        }
    }
}