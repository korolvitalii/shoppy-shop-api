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
                x.Product.InStock))
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(Guid userId, string productId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Products.AnyAsync(x => x.Id == productId, cancellationToken))
        {
            throw new AppNotFoundException("Product was not found.");
        }

        if (await dbContext.Favorites.AnyAsync(x => x.UserId == userId && x.ProductId == productId, cancellationToken))
        {
            return;
        }

        // GetAsync returns the whole collection unpaged, so its cost is chosen by whoever wrote it,
        // and the unique (UserId, ProductId) key caps that at the catalogue size. Against today's 54
        // seeded products this bound is unreachable and the count is pure overhead; it is here for
        // the 100k-product catalogue the pagination work is aimed at, and should be revisited if
        // that never arrives. Checked after the duplicate test so re-saving an existing favourite at
        // the limit still succeeds. 422 matches the checkout bounds — same class of violation.
        if (await dbContext.Favorites.CountAsync(x => x.UserId == userId, cancellationToken) >= MaxFavoritesPerUser)
        {
            throw new AppUnprocessableException($"A maximum of {MaxFavoritesPerUser} favourites can be saved.");
        }

        dbContext.Favorites.Add(new Favorite
        {
            UserId = userId,
            ProductId = productId,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The check above is not atomic, so two concurrent taps can both reach the
            // insert and the loser violates the primary key. Adding a favourite is
            // idempotent, so confirm the row landed and treat that as success.
            dbContext.ChangeTracker.Clear();
            if (!await dbContext.Favorites.AnyAsync(x => x.UserId == userId && x.ProductId == productId, cancellationToken))
            {
                throw;
            }
        }
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