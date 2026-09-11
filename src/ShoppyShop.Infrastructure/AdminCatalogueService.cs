using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class AdminCatalogueService(AppDbContext dbContext) : IAdminCatalogueService
{
    // Prices are stored as numeric(12,2). PostgreSQL rounds anything finer to that scale without
    // complaint, so 0.004 would pass a ">0" check and persist as 0.00 — a free product at checkout,
    // which reads the stored price rather than the submitted one.
    private const decimal MaxPrice = CommerceLimits.MaxProductPrice;

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
        ValidateId(id, request.Id);
        ValidateMoney(request.Price, nameof(request.Price));
        if (request.SalePrice is { } salePrice)
        {
            ValidateMoney(salePrice, nameof(request.SalePrice));
        }

        if (request.SalePrice >= request.Price)
        {
            throw new AppUnprocessableException("Price must be positive and sale price must be lower than price.");
        }

        if (!await dbContext.ProductGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == request.GroupId && !x.IsDeleted, cancellationToken))
        {
            throw new AppUnprocessableException("The product group does not exist.");
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

    private static void ValidateMoney(decimal value, string field)
    {
        if (decimal.Round(value, 2) != value)
        {
            throw new AppUnprocessableException($"{field} must have at most two decimal places.");
        }

        if (value <= 0 || value > MaxPrice)
        {
            throw new AppUnprocessableException($"{field} must be greater than zero and at most {MaxPrice:0.00}.");
        }
    }

    private static string Required(string value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new AppValidationException($"{field} is required.")
            : value.Trim();
}