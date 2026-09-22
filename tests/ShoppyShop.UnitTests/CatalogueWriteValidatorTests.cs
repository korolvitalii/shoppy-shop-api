using ShoppyShop.Application;

namespace ShoppyShop.UnitTests;

/// <summary>
/// The admin write rules exercised directly, with no database. <c>AdminCatalogueServiceTests</c>
/// covers the checks that need one, such as whether the product group exists.
/// </summary>
public sealed class CatalogueWriteValidatorTests
{
    [Fact]
    public void ValidateProductTrimsTheStringFields()
    {
        var validated = CatalogueWriteValidator.ValidateProduct(
            "product-1",
            Product(price: 10m) with { Name = "  Name  ", Brand = " Brand " });

        Assert.Equal("Name", validated.Name);
        Assert.Equal("Brand", validated.Brand);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.004)]
    [InlineData(10.005)]
    [InlineData(10_000.01)]
    public void ValidateProductRejectsPricesTheMoneyColumnCannotStoreOrTheShopWouldNotList(decimal price)
    {
        Assert.Throws<AppUnprocessableException>(() =>
            CatalogueWriteValidator.ValidateProduct("product-1", Product(price)));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void ValidateProductRejectsASalePriceThatIsNotADiscount(decimal salePrice)
    {
        Assert.Throws<AppUnprocessableException>(() =>
            CatalogueWriteValidator.ValidateProduct("product-1", Product(10m, salePrice)));
    }

    [Fact]
    public void ValidateProductRejectsMismatchedRouteAndBodyIds()
    {
        Assert.Throws<AppValidationException>(() =>
            CatalogueWriteValidator.ValidateProduct("product-2", Product(10m)));
    }

    [Fact]
    public void ValidateProductRejectsANullRequiredField()
    {
        // Declared non-nullable, but the JSON binder yields null for an absent member.
        Assert.Throws<AppValidationException>(() =>
            CatalogueWriteValidator.ValidateProduct("product-1", Product(10m) with { Brand = null! }));
    }

    [Fact]
    public void ValidateProductRejectsAnOverlongDescription()
    {
        Assert.Throws<AppUnprocessableException>(() => CatalogueWriteValidator.ValidateProduct(
            "product-1",
            Product(10m) with { Description = new string('d', 4_001) }));
    }

    [Fact]
    public void ValidateGroupTreatsABlankBadgeAsNone()
    {
        var validated = CatalogueWriteValidator.ValidateGroup(
            "beauty",
            new ProductGroupWriteRequest("beauty", "Beauty", "Description", "/g.jpg", "   ", 1));

        Assert.Null(validated.Badge);
    }

    [Fact]
    public void ValidateGroupRejectsAnOverlongBadge()
    {
        Assert.Throws<AppUnprocessableException>(() => CatalogueWriteValidator.ValidateGroup(
            "beauty",
            new ProductGroupWriteRequest("beauty", "Beauty", "Description", "/g.jpg", new string('b', 81), 1)));
    }

    private static ProductWriteRequest Product(decimal price, decimal? salePrice = null) =>
        new("product-1", "beauty", "Name", "Brand", "Description", "/i.jpg", price, salePrice, true);
}