using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class AdminCatalogueService(AppDbContext dbContext) : IAdminCatalogueService
{
    public async Task<ProductGroupDto> UpsertGroupAsync(
        string id,
        ProductGroupWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateId(id, request.Id);
        var group = await dbContext.ProductGroups.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (group is null)
        {
            group = new ProductGroup
            {
                Id = id,
                Name = request.Name.Trim(),
                Description = request.Description.Trim(),
                ImageUrl = request.ImageUrl.Trim(),
            };
            dbContext.ProductGroups.Add(group);
        }

        group.Name = Required(request.Name, nameof(request.Name));
        group.Description = Required(request.Description, nameof(request.Description));
        group.ImageUrl = Required(request.ImageUrl, nameof(request.ImageUrl));
        group.Badge = request.Badge?.Trim();
        group.DisplayOrder = request.DisplayOrder;
        group.IsDeleted = false;
        await dbContext.SaveChangesAsync(cancellationToken);

        var count = await dbContext.Products.IgnoreQueryFilters().CountAsync(x => x.GroupId == id && !x.IsDeleted, cancellationToken);
        return new ProductGroupDto(group.Id, group.Name, group.Description, group.ImageUrl, count, group.Badge);
    }

    public async Task DeleteGroupAsync(string id, CancellationToken cancellationToken)
    {
        var group = await dbContext.ProductGroups.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Product group was not found.");
        group.IsDeleted = true;
        await dbContext.Products.IgnoreQueryFilters()
            .Where(x => x.GroupId == id)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDeleted, true), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProductDto> UpsertProductAsync(
        string id,
        ProductWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateId(id, request.Id);
        if (request.Price <= 0 || request.SalePrice is <= 0 || request.SalePrice >= request.Price)
        {
            throw new AppValidationException("Price must be positive and sale price must be lower than price.");
        }

        if (!await dbContext.ProductGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == request.GroupId && !x.IsDeleted, cancellationToken))
        {
            throw new AppValidationException("The product group does not exist.");
        }

        var product = await dbContext.Products.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (product is null)
        {
            product = new Product
            {
                Id = id,
                GroupId = request.GroupId,
                Name = request.Name.Trim(),
                Brand = request.Brand.Trim(),
                Description = request.Description.Trim(),
                ImageUrl = request.ImageUrl.Trim(),
            };
            dbContext.Products.Add(product);
        }

        product.GroupId = Required(request.GroupId, nameof(request.GroupId));
        product.Name = Required(request.Name, nameof(request.Name));
        product.Brand = Required(request.Brand, nameof(request.Brand));
        product.Description = Required(request.Description, nameof(request.Description));
        product.ImageUrl = Required(request.ImageUrl, nameof(request.ImageUrl));
        product.Price = request.Price;
        product.SalePrice = request.SalePrice;
        product.InStock = request.InStock;
        product.IsDeleted = false;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ProductDto(
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

    public async Task DeleteProductAsync(string id, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Product was not found.");
        product.IsDeleted = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateId(string routeId, string bodyId)
    {
        if (string.IsNullOrWhiteSpace(routeId) || !string.Equals(routeId, bodyId, StringComparison.Ordinal))
        {
            throw new AppValidationException("Route and body identifiers must match.");
        }
    }

    private static string Required(string value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new AppValidationException($"{field} is required.")
            : value.Trim();
}