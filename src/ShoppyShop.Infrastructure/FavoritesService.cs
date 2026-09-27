using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class FavoritesService(AppDbContext dbContext, TimeProvider timeProvider) : IFavoritesService
{
    private const int MaxFavoritesPerUser = 500;

    public async Task<IReadOnlyCollection<ProductDto>> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Favorites.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ProductDto(
                x.Product!.Id,
                x.Product.GroupId,
                x.Product.Name,
                x.Product.Brand,
                x.Product.Description,
                x.Product.ImageUrl,
                x.Product.Price,
                x.Product.SalePrice,
                x.Product.InStock,
                x.Product.IsNew,
                x.Product.GiftWrappable))
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(Guid userId, string productId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Products.AnyAsync(x => x.Id == productId, cancellationToken))
        {
            throw new AppNotFoundException("Product was not found.");
        }

        // The duplicate test, the count and the insert used to be separate statements, so adds
        // running at once for different products could all count 499 and all insert, taking the
        // account past the cap. Holding the account's lock across all three makes concurrent adds
        // take turns, so the cap is exact. It also settles two taps on the same product: the second
        // finds the first's row, where it used to hit the primary key and recover from the error.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await dbContext.Database.AcquireTransactionLockAsync(AdvisoryLocks.Favorites, userId.ToString(), ct);

            if (await dbContext.Favorites.AnyAsync(x => x.UserId == userId && x.ProductId == productId, ct))
            {
                return;
            }

            // GetAsync returns the whole collection unpaged, so its cost is chosen by whoever wrote
            // it, and the unique (UserId, ProductId) key caps that at the catalogue size. Against
            // today's 90 seeded products this bound is unreachable and the count is pure overhead; it
            // is here for the 100k-product catalogue the pagination work is aimed at, and should be
            // revisited if that never arrives. Checked after the duplicate test so re-saving an
            // existing favourite at the limit still succeeds. 422 matches the checkout bounds, the
            // same class of violation.
            if (await dbContext.Favorites.CountAsync(x => x.UserId == userId, ct) >= MaxFavoritesPerUser)
            {
                throw new AppUnprocessableException($"A maximum of {MaxFavoritesPerUser} favourites can be saved.");
            }

            dbContext.Favorites.Add(new Favorite
            {
                UserId = userId,
                ProductId = productId,
                CreatedAt = timeProvider.GetUtcNow(),
            });
            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    public async Task RemoveAsync(Guid userId, string productId, CancellationToken cancellationToken)
    {
        await dbContext.Favorites
            .Where(x => x.UserId == userId && x.ProductId == productId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearAsync(Guid userId, CancellationToken cancellationToken)
    {
        await dbContext.Favorites.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }
}