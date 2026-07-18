using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class FavoritesService(AppDbContext dbContext, TimeProvider timeProvider) : IFavoritesService
{
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

        if (!await dbContext.Favorites.AnyAsync(x => x.UserId == userId && x.ProductId == productId, cancellationToken))
        {
            dbContext.Favorites.Add(new Favorite
            {
                UserId = userId,
                ProductId = productId,
                CreatedAt = timeProvider.GetUtcNow(),
            });
            await dbContext.SaveChangesAsync(cancellationToken);
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