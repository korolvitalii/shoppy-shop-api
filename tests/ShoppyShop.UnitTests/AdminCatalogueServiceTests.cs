using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

public sealed class AdminCatalogueServiceTests
{
    [Fact]
    public async Task UpsertProductAsyncRejectsASalePriceThatIsNotLowerThanThePrice()
    {
        using var fixture = new SqliteAppDbContextFixture();
        fixture.DbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        await fixture.DbContext.SaveChangesAsync();
        var service = new AdminCatalogueService(fixture.DbContext);

        await Assert.ThrowsAsync<AppUnprocessableException>(() => service.UpsertProductAsync(
            "product-1",
            new ProductWriteRequest("product-1", "beauty", "Name", "Brand", "Description", "/i.jpg", 10m, 20m, true),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpsertProductAsyncRejectsAnUnknownProductGroup()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var service = new AdminCatalogueService(fixture.DbContext);

        await Assert.ThrowsAsync<AppUnprocessableException>(() => service.UpsertProductAsync(
            "product-1",
            new ProductWriteRequest("product-1", "missing-group", "Name", "Brand", "Description", "/i.jpg", 10m, null, true),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpsertGroupAsyncRejectsARouteAndBodyIdentifierMismatch()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var service = new AdminCatalogueService(fixture.DbContext);

        await Assert.ThrowsAsync<AppValidationException>(() => service.UpsertGroupAsync(
            "route-id",
            new ProductGroupWriteRequest("body-id", "Name", "Description", "/g.jpg", null, 0),
            CancellationToken.None));
    }

    [Fact]
    public async Task DeleteGroupAsyncSoftDeletesTheGroupAndItsProductsSoOrderHistoryStaysValid()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.Add(new Product
        {
            Id = "product-1",
            GroupId = "beauty",
            Name = "Name",
            Brand = "Brand",
            Description = "d",
            ImageUrl = "/i.jpg",
            Price = 10m,
            InStock = true,
        });
        await dbContext.SaveChangesAsync();
        var service = new AdminCatalogueService(dbContext);

        await service.DeleteGroupAsync("beauty", CancellationToken.None);

        Assert.False(await dbContext.ProductGroups.AnyAsync(x => x.Id == "beauty"));
        Assert.False(await dbContext.Products.AnyAsync(x => x.Id == "product-1"));

        // ExecuteUpdateAsync bypasses the change tracker, so re-reading through the same DbContext
        // instance requires AsNoTracking() — otherwise the already-tracked "product" entity (added
        // above) would be returned unchanged instead of reflecting the update.
        var product = await dbContext.Products.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == "product-1");
        Assert.True(product.IsDeleted);
    }
}