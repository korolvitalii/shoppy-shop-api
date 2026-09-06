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
        Assert.Equal(["pricey", "mid", "cheap"], descending.Items.Select(x => x.Id));

        var ascending = await service.GetProductsAsync(new ProductQuery(Sort: "price-asc"), CancellationToken.None);
        Assert.Equal(["cheap", "mid", "pricey"], ascending.Items.Select(x => x.Id));

        var midRange = await service.GetProductsAsync(new ProductQuery(MinPrice: 50m, MaxPrice: 200m), CancellationToken.None);
        Assert.Equal(["mid"], midRange.Items.Select(x => x.Id));
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
        Assert.Equal(2, products.Items.Count);
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

        Assert.Equal(["waterproof-jacket"], products.Items.Select(product => product.Id));
    }

    [Theory]
    [InlineData(ProductSorts.Featured)]
    [InlineData(ProductSorts.PriceAscending)]
    [InlineData(ProductSorts.PriceDescending)]
    [InlineData(ProductSorts.Name)]
    public async Task GetProductsAsyncPagesThroughTiedSortKeysWithoutDroppingOrRepeatingRows(string sort)
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });

        // Three rows share a price and a name. A seek predicate that compared only the sort key —
        // without the id tiebreak — would either skip the rest of the tied run or serve it twice
        // whenever a page boundary landed in the middle of it.
        dbContext.Products.AddRange(
            Product("p1", price: 50m, salePrice: null, name: "Tied"),
            Product("p2", price: 50m, salePrice: null, name: "Tied"),
            Product("p3", price: 50m, salePrice: null, name: "Tied"),
            Product("p4", price: 10m, salePrice: null, name: "Cheapest"),
            Product("p5", price: 90m, salePrice: null, name: "Priciest"));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var unpaged = await service.GetProductsAsync(new ProductQuery(Sort: sort), CancellationToken.None);
        var paged = await DrainAsync(service, sort, pageSize: 2);

        Assert.Equal(unpaged.Items.Select(x => x.Id), paged);
        Assert.Equal(5, paged.Count);
    }

    [Fact]
    public async Task GetProductsAsyncEndsWithoutAnEmptyPageWhenTheLastPageIsExactlyFull()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(
            Product("p1", price: 10m, salePrice: null),
            Product("p2", price: 20m, salePrice: null),
            Product("p3", price: 30m, salePrice: null),
            Product("p4", price: 40m, salePrice: null));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var first = await service.GetProductsAsync(new ProductQuery(Limit: 2), CancellationToken.None);
        Assert.NotNull(first.NextCursor);

        var second = await service.GetProductsAsync(
            new ProductQuery(Cursor: first.NextCursor, Limit: 2),
            CancellationToken.None);

        Assert.Equal(["p3", "p4"], second.Items.Select(x => x.Id));

        // Reading one row past the page is what lets this page know it is the last one, so the client
        // never has to make a further request that comes back empty.
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task GetProductsAsyncCapsAnUnboundedRequestAtTheDefaultPageSize()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(Enumerable.Range(1, 30)
            .Select(index => Product($"p{index:D2}", price: index, salePrice: null)));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var page = await service.GetProductsAsync(new ProductQuery(), CancellationToken.None);

        Assert.Equal(24, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task GetGroupProductsAsyncKeepsLaterPagesInsideTheGroup()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.AddRange(
            new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" },
            new ProductGroup { Id = "home", Name = "Home", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(
            Product("b1", price: 10m, salePrice: null),
            Product("b2", price: 20m, salePrice: null),
            Product("b3", price: 30m, salePrice: null),
            Product("h1", price: 15m, salePrice: null, groupId: "home"),
            Product("h2", price: 25m, salePrice: null, groupId: "home"));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var ids = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await service.GetGroupProductsAsync(
                "beauty",
                new ProductQuery(Cursor: cursor, Limit: 2),
                CancellationToken.None);
            ids.AddRange(result.Items.Select(x => x.Id));
            cursor = result.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(["b1", "b2", "b3"], ids);
    }

    [Fact]
    public async Task GetProductsAsyncRejectsACursorIssuedUnderADifferentSort()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var dbContext = fixture.DbContext;
        dbContext.ProductGroups.Add(new ProductGroup { Id = "beauty", Name = "Beauty", Description = "d", ImageUrl = "/g.jpg" });
        dbContext.Products.AddRange(
            Product("p1", price: 10m, salePrice: null, name: "Alpha"),
            Product("p2", price: 20m, salePrice: null, name: "Beta"));
        await dbContext.SaveChangesAsync();
        var service = new CatalogueService(dbContext);

        var byName = await service.GetProductsAsync(
            new ProductQuery(Sort: ProductSorts.Name, Limit: 1),
            CancellationToken.None);
        Assert.NotNull(byName.NextCursor);

        // That cursor carries a name. Replaying it under a price sort would compare the name against
        // a price column and quietly return the wrong window, so it is refused rather than guessed at.
        await Assert.ThrowsAsync<AppValidationException>(() => service.GetProductsAsync(
            new ProductQuery(Sort: ProductSorts.PriceAscending, Cursor: byName.NextCursor, Limit: 1),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("eyJzIjoiZmVhdHVyZWQifQ")]
    public async Task GetProductsAsyncRejectsAMalformedCursor(string cursor)
    {
        using var fixture = new SqliteAppDbContextFixture();
        var service = new CatalogueService(fixture.DbContext);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            service.GetProductsAsync(new ProductQuery(Cursor: cursor), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public async Task GetProductsAsyncRejectsAPageSizeOutsideTheAllowedRange(int limit)
    {
        using var fixture = new SqliteAppDbContextFixture();
        var service = new CatalogueService(fixture.DbContext);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            service.GetProductsAsync(new ProductQuery(Limit: limit), CancellationToken.None));
    }

    private static async Task<IReadOnlyList<string>> DrainAsync(CatalogueService service, string sort, int pageSize)
    {
        var ids = new List<string>();
        string? cursor = null;

        // Bounded: a cursor that failed to advance would otherwise spin here forever instead of
        // failing the assertion.
        for (var page = 0; page < 20; page++)
        {
            var result = await service.GetProductsAsync(
                new ProductQuery(Sort: sort, Cursor: cursor, Limit: pageSize),
                CancellationToken.None);
            ids.AddRange(result.Items.Select(x => x.Id));
            cursor = result.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        return ids;
    }

    private static Product Product(
        string id,
        decimal price,
        decimal? salePrice,
        bool isDeleted = false,
        string? name = null,
        string groupId = "beauty") => new()
    {
        Id = id,
        GroupId = groupId,
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