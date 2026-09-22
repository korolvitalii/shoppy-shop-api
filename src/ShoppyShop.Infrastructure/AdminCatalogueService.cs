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
        var (name, description, imageUrl, badge) = CatalogueWriteValidator.ValidateGroup(id, request);

        var group = await dbContext.ProductGroups.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (group is null)
        {
            group = new ProductGroup { Id = id, Name = name, Description = description, ImageUrl = imageUrl };
            dbContext.ProductGroups.Add(group);
        }

        group.Name = name;
        group.Description = description;
        group.ImageUrl = imageUrl;
        group.Badge = badge;
        group.DisplayOrder = request.DisplayOrder;
        group.IsDeleted = false;
        await dbContext.SaveChangesAsync(cancellationToken);

        var count = await dbContext.Products.IgnoreQueryFilters().CountAsync(x => x.GroupId == id && !x.IsDeleted, cancellationToken);
        return new ProductGroupDto(group.Id, group.Name, group.Description, group.ImageUrl, count, group.Badge);
    }

    public async Task DeleteGroupAsync(string id, CancellationToken cancellationToken)
    {
        // ExecuteUpdateAsync commits on its own the moment it is awaited, so without an
        // explicit transaction the products and the group would be soft-deleted in two
        // independent commits — a failure between them leaves a live group with no
        // visible products, and nothing repairs that state.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            var group = await dbContext.ProductGroups.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new AppNotFoundException("Product group was not found.");

            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            group.IsDeleted = true;
            await dbContext.Products.IgnoreQueryFilters()
                .Where(x => x.GroupId == id)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDeleted, true), ct);
            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    public async Task<ProductDto> UpsertProductAsync(
        string id,
        ProductWriteRequest request,
        CancellationToken cancellationToken)
    {
        var (groupId, name, brand, description, imageUrl) = CatalogueWriteValidator.ValidateProduct(id, request);

        if (!await dbContext.ProductGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == groupId && !x.IsDeleted, cancellationToken))
        {
            throw new AppUnprocessableException("The product group does not exist.");
        }

        var product = await dbContext.Products.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (product is null)
        {
            product = new Product { Id = id, GroupId = groupId, Name = name, Brand = brand, Description = description, ImageUrl = imageUrl };
            dbContext.Products.Add(product);
        }

        product.GroupId = groupId;
        product.Name = name;
        product.Brand = brand;
        product.Description = description;
        product.ImageUrl = imageUrl;
        product.Price = request.Price;
        product.SalePrice = request.SalePrice;
        product.InStock = request.InStock;
        product.IsNew = request.IsNew;
        product.GiftWrappable = request.GiftWrappable;
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
            product.InStock,
            product.IsNew,
            product.GiftWrappable);
    }

    public async Task DeleteProductAsync(string id, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Product was not found.");
        product.IsDeleted = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}