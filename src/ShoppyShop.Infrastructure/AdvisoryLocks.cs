using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ShoppyShop.Infrastructure;

/// <summary>
/// Transaction-scoped advisory locks keyed by a lock space and a string id.
/// </summary>
/// <remarks>
/// The two-key form of <c>pg_advisory_xact_lock</c>: the first key is the space, listed here so two
/// features never pick the same one, and the second is <c>hashtext</c> of the id. Postgres keeps the
/// two-key form apart from the single 64-bit keys taken elsewhere (AuthService's per-user session
/// lock, the startup initializer, the cleanup service), so they can never collide. A hash collision
/// between two ids only makes them take turns. SQLite (unit tests) has no equivalent and no
/// concurrency to serialize, so the lock is skipped there, as in AuthService.
/// </remarks>
internal static class AdvisoryLocks
{
    /// <summary>Per category: AdminCatalogueService's category delete against product saves.</summary>
    public const int CatalogueGroup = 1;

    /// <summary>Per user: FavoritesService's count-and-insert against the favourites cap.</summary>
    public const int Favorites = 2;

    /// <summary>Holds the lock until the caller's current transaction ends.</summary>
    public static async Task AcquireTransactionLockAsync(
        this DatabaseFacade database,
        int space,
        string id,
        CancellationToken cancellationToken)
    {
        if (!database.IsNpgsql())
        {
            return;
        }

        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({space}, hashtext({id}))",
            cancellationToken);
    }
}