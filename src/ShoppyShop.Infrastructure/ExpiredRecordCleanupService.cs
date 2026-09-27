using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShoppyShop.Infrastructure;

/// <summary>
/// Deletes refresh sessions and order idempotency records once they can no longer affect a request.
/// </summary>
/// <remarks>
/// <para>
/// Both tables only ever grew. Every sign-in and every refresh inserts a <c>RefreshSessions</c> row
/// (the storefront refreshes on each page load of a signed-in visitor), every order inserts an
/// <c>OrderRequests</c> row, and nothing deleted either once it had expired.
/// </para>
/// <para>
/// Runs at startup and then hourly, on every instance. Each batch is its own short transaction that
/// first tries a transaction-scoped advisory lock and ends the sweep if another instance holds it,
/// so replicas do not repeat each other's work. The lock is released with its transaction rather than
/// held by a session, which keeps it correct behind a transaction-mode pooler as well.
/// </para>
/// </remarks>
public sealed partial class ExpiredRecordCleanupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<ExpiredRecordCleanupService> logger) : BackgroundService
{
    public const string EnabledSetting = "Maintenance:CleanupEnabled";
    public const int BatchSize = 1_000;

    // One sweep deletes at most this many batches per table, so a large first backlog is worked
    // through over a few hourly runs instead of in one long burst of writes.
    private const int MaxBatchesPerTable = 100;
    private const long CleanupLockKey = 2026092701;

    // RefreshAsync checks expiry before anything else, so an expired session and a deleted one get
    // the same 401. The extra day only keeps a just-expired row around to inspect when someone
    // reports being signed out.
    private static readonly TimeSpan RefreshSessionRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(EnabledSetting, true))
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                var result = await SweepAsync(stoppingToken);
                if (result.RefreshSessions > 0 || result.OrderRequests > 0)
                {
                    LogSwept(result.RefreshSessions, result.OrderRequests);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // An exception escaping a BackgroundService stops the whole host, and housekeeping is
                // not worth an outage. The next tick tries again.
                LogSweepFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass over both tables. Public so tests can run it without waiting for the timer.
    /// </summary>
    public async Task<ExpiredRecordCleanupResult> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!dbContext.Database.IsNpgsql())
        {
            return default;
        }

        var now = timeProvider.GetUtcNow();
        var sessionCutoff = now - RefreshSessionRetention;

        var sessions = await DeleteInBatchesAsync(
            dbContext,
            ct => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM "RefreshSessions" WHERE "Id" IN (
                    SELECT "Id" FROM "RefreshSessions" WHERE "ExpiresAt" < {sessionCutoff} LIMIT {BatchSize})
                """,
                ct),
            cancellationToken);
        if (sessions.Yielded)
        {
            return new ExpiredRecordCleanupResult(sessions.Deleted, 0);
        }

        // An expired request is one OrdersService would delete and replace anyway on the next order
        // with the same key. Deleting it here while that happens is safe: checkout runs serializable,
        // so its own delete of the same row fails with a serialization error and is retried.
        var requests = await DeleteInBatchesAsync(
            dbContext,
            ct => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM "OrderRequests" WHERE ("UserId", "Key") IN (
                    SELECT "UserId", "Key" FROM "OrderRequests" WHERE "ExpiresAt" < {now} LIMIT {BatchSize})
                """,
                ct),
            cancellationToken);

        return new ExpiredRecordCleanupResult(sessions.Deleted, requests.Deleted);
    }

    private static async Task<(int Deleted, bool Yielded)> DeleteInBatchesAsync(
        AppDbContext dbContext,
        Func<CancellationToken, Task<int>> deleteBatch,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerTable; batch++)
        {
            var deleted = await strategy.ExecuteAsync(async ct =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
                var acquired = await dbContext.Database
                    .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({CleanupLockKey}) AS \"Value\"")
                    .SingleAsync(ct);
                if (!acquired)
                {
                    return (int?)null;
                }

                var count = await deleteBatch(ct);
                await transaction.CommitAsync(ct);
                return count;
            }, cancellationToken);

            if (deleted is null)
            {
                return (total, true);
            }

            total += deleted.Value;
            if (deleted.Value < BatchSize)
            {
                break;
            }
        }

        return (total, false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Deleted {RefreshSessions} expired refresh sessions and {OrderRequests} expired order requests")]
    private partial void LogSwept(int refreshSessions, int orderRequests);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Expired record cleanup failed; retrying at the next interval")]
    private partial void LogSweepFailed(Exception exception);
}

/// <summary>Rows deleted by one <see cref="ExpiredRecordCleanupService.SweepAsync"/> pass.</summary>
public readonly record struct ExpiredRecordCleanupResult(int RefreshSessions, int OrderRequests);