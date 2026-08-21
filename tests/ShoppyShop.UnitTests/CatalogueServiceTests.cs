using ShoppyShop.Application;
using ShoppyShop.Domain;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

public sealed class CatalogueServiceTests
{
    [Fact]
    public async Task GetProductsAsyncFiltersByPriceRangeAndSortsByEffectivePrice()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(
            Product("cheap", price: 10m, salePrice: null),
            Product("mid", price: 100m, salePrice: 60m),
            Product("pricey", price: 300m, salePrice: null),
            Product("deleted", price: 20m, salePrice: null, isDeleted: true));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var descending = await service.GetProductsAsync(new ProductQuery(Sort: "price-desc"), CancellationToken.None);
        Assert.Equal(["pricey", "mid", "cheap"], descending.Select(x => x.Id));

        var ascending = await service.GetProductsAsync(new ProductQuery(Sort: "price-asc"), CancellationToken.None);
        Assert.Equal(["cheap", "mid", "pricey"], ascending.Select(x => x.Id));

        var midRange = await service.GetProductsAsync(new ProductQuery(MinPrice: 50m, MaxPrice: 200m), CancellationToken.None);
        Assert.Equal(["mid"], midRange.Select(x => x.Id));
    }

    [Fact]
    public async Task GetGroupsAsyncCountsOnlyActiveProductsUnlessDeletedAreIncluded()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(
            Product("active", price: 10m, salePrice: null),
            Product("gone", price: 10m, salePrice: null, isDeleted: true));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var publicGroups = await service.GetGroupsAsync(includeDeleted: false, CancellationToken.None);
        Assert.Equal(1, publicGroups.Single().ItemCount);

        var products = await service.GetProductsAsync(new ProductQuery(IncludeDeleted: true), CancellationToken.None);
        Assert.Equal(2, products.Count);
    }

    [Fact]
    public async Task GetProductsAsyncMatchesSeparateKeywordsAndSimplePlurals()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(
            Product("waterproof-jacket", price: 80m, salePrice: null, name: "Waterproof Jacket"),
            Product("unrelated", price: 20m, salePrice: null, name: "Ceramic Table"));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var products = await service.GetProductsAsync(new ProductQuery(Search: "water proof jackets"), CancellationToken.None);

        Assert.Equal(["waterproof-jacket"], products.Select(product => product.Id));
    }

    private static Product Product(string id, decimal price, decimal? salePrice, bool isDeleted = false, string? name = null) => new()
    {
        Id = id,
        GroupId = "beauty",
        Name = name ?? id,
        Brand = "Test brand",
        Description = "Test description",
        ImageUrl = "/product.jpg",
        Price = price,
        SalePrice = salePrice,
        InStock = true,
        IsDeleted = isDeleted,
    };
}