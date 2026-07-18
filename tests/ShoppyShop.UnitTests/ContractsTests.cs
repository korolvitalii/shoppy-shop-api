using ShoppyShop.Application;

namespace ShoppyShop.UnitTests;

public sealed class ContractsTests
{
    [Fact]
    public void OrderLineTotalIsDerivedFromServerPriceAndQuantity()
    {
        var line = new OrderLineDto("product-1", "beauty", "Product", "/image.jpg", 12.50m, 3);

        Assert.Equal(37.50m, line.UnitPrice * line.Quantity);
    }

    [Fact]
    public void ProductQueryDefaultsToPublicCatalogue()
    {
        var query = new ProductQuery();

        Assert.False(query.IncludeDeleted);
        Assert.Null(query.Search);
    }
}