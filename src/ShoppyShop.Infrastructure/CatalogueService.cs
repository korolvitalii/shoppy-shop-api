using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class CatalogueService(AppDbContext dbContext) : ICatalogueService
{
    public async Task<IReadOnlyCollection<ProductGroupDto>> GetGroupsAsync(bool includeDeleted, CancellationToken cancellationToken)
    {
        var groups = includeDeleted
            ? dbContext.ProductGroups.IgnoreQueryFilters()
            : dbContext.ProductGroups;

        return await groups.AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new ProductGroupDto(
                x.Id,
                x.Name,
                x.Description,
                x.ImageUrl,
                x.Products.Count(product => !product.IsDeleted),
                x.Badge))
            .ToArrayAsync(cancellationToken);
    }

    public Task<IReadOnlyCollection<ProductDto>> GetProductsAsync(ProductQuery query, CancellationToken cancellationToken) =>
        ExecuteProductQueryAsync(query, null, cancellationToken);

    public Task<IReadOnlyCollection<ProductDto>> GetGroupProductsAsync(
        string groupId,
        ProductQuery query,
        CancellationToken cancellationToken) => ExecuteProductQueryAsync(query, groupId, cancellationToken);

    public async Task<ProductDto?> GetProductAsync(
        string groupId,
        string productId,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        var products = includeDeleted ? dbContext.Products.IgnoreQueryFilters() : dbContext.Products;
        return await products.AsNoTracking()
            .Where(x => x.GroupId == groupId && x.Id == productId)
            .Select(ProductProjection)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<ProductDto>> ExecuteProductQueryAsync(
        ProductQuery query,
        string? groupId,
        CancellationToken cancellationToken)
    {
        IQueryable<Product> products = query.IncludeDeleted
            ? dbContext.Products.IgnoreQueryFilters()
            : dbContext.Products;

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            products = products.Where(x => x.GroupId == groupId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            products = products.Where(x =>
                EF.Functions.ILike(x.Name, $"%{search}%") ||
                EF.Functions.ILike(x.Brand, $"%{search}%") ||
                EF.Functions.ILike(x.Description, $"%{search}%"));
        }

        if (query.MinPrice is not null)
        {
            products = products.Where(x => (x.SalePrice ?? x.Price) >= query.MinPrice);
        }

        if (query.MaxPrice is not null)
        {
            products = products.Where(x => (x.SalePrice ?? x.Price) <= query.MaxPrice);
        }

        products = query.Sort?.ToLowerInvariant() switch
        {
            "price-asc" => products.OrderBy(x => x.SalePrice ?? x.Price),
            "price-desc" => products.OrderByDescending(x => x.SalePrice ?? x.Price),
            "name" => products.OrderBy(x => x.Name),
            _ => products.OrderBy(x => x.GroupId).ThenBy(x => x.Id),
        };

        return await products.AsNoTracking().Select(ProductProjection).ToArrayAsync(cancellationToken);
    }

    private static System.Linq.Expressions.Expression<Func<Product, ProductDto>> ProductProjection => product =>
        new ProductDto(
            product.Id,
            product.GroupId,
            product.Name,
            product.Brand,
            product.Description,
            product.ImageUrl,
            product.Price,
            product.SalePrice,
            product.InStock);
}