using ShoppyShop.Application;
using ShoppyShop.Domain;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

public sealed class OrdersServiceTests
{
    [Fact]
    public async Task CreateAsyncRecalculatesTotalsFromServerPricesAndFormatsTheOrderNumber()
    {
        using var fixture = new SqliteAppDbContextFixture();
        SeedProduct(fixture.DbContext, "beauty-1", "beauty", price: 120m, salePrice: 99.99m);
        var service = new OrdersService(fixture.DbContext, TimeProvider.System);

        var order = await service.CreateAsync(
            Guid.NewGuid(),
            "key-1",
            CreateRequest("beauty-1", "beauty", clientUnitPrice: 0.01m, quantity: 2),
            CancellationToken.None);

        // The client-submitted unit price is never trusted: totals are derived from the stored sale price.
        Assert.Equal(99.99m, order.Lines.Single().UnitPrice);
        Assert.Equal(199.98m, order.Subtotal);
        Assert.Equal(4.99m, order.DeliveryCharge);
        Assert.Equal(204.97m, order.Total);
        Assert.Matches(@"^ORD-\d{5,}$", order.Id);
    }

    [Fact]
    public async Task GetAsyncFormatsAndParsesTheOrderNumberAndEnforcesOwnership()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var userId = Guid.NewGuid();
        fixture.DbContext.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = 42,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Currency = "GBP",
            Status = "confirmed",
            DeliveryName = "Test Customer",
            DeliveryEmail = "customer@example.test",
            DeliveryAddressLine1 = "1 Test Street",
            DeliveryCity = "London",
            DeliveryPostcode = "SW1A 1AA",
            DeliveryCountry = "United Kingdom",
            DeliveryMethod = "standard",
            PaymentTokenId = "tok_test_only",
            PaymentBrand = "Visa",
            PaymentLast4 = "4242",
            Subtotal = 10m,
            DeliveryCharge = 4.99m,
            Total = 14.99m,
        });
        await fixture.DbContext.SaveChangesAsync();
        var service = new OrdersService(fixture.DbContext, TimeProvider.System);

        var found = await service.GetAsync(userId, "ORD-00042", CancellationToken.None);
        Assert.Equal("ORD-00042", found!.Id);

        Assert.Null(await service.GetAsync(userId, "not-an-order-id", CancellationToken.None));
        Assert.Null(await service.GetAsync(Guid.NewGuid(), "ORD-00042", CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsyncReplaysTheSameOrderForARepeatedIdempotencyKeyAndRejectsAMismatchedRetry()
    {
        using var fixture = new SqliteAppDbContextFixture();
        SeedProduct(fixture.DbContext, "beauty-1", "beauty", price: 50m, salePrice: null);
        var service = new OrdersService(fixture.DbContext, TimeProvider.System);
        var userId = Guid.NewGuid();
        var request = CreateRequest("beauty-1", "beauty", clientUnitPrice: 999m, quantity: 1);

        var first = await service.CreateAsync(userId, "same-key", request, CancellationToken.None);
        var retried = await service.CreateAsync(userId, "same-key", request, CancellationToken.None);
        Assert.Equal(first.Id, retried.Id);

        var mismatched = CreateRequest("beauty-1", "beauty", clientUnitPrice: 999m, quantity: 2);
        await Assert.ThrowsAsync<AppConflictException>(
            () => service.CreateAsync(userId, "same-key", mismatched, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsyncRejectsOrdersForProductsThatDoNotExist()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var service = new OrdersService(fixture.DbContext, TimeProvider.System);

        await Assert.ThrowsAsync<AppUnprocessableException>(() => service.CreateAsync(
            Guid.NewGuid(),
            "key",
            CreateRequest("missing-product", "beauty", clientUnitPrice: 10m, quantity: 1),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task CreateAsyncRejectsQuantitiesOutsideTheAllowedRange(int quantity)
    {
        using var fixture = new SqliteAppDbContextFixture();
        SeedProduct(fixture.DbContext, "beauty-1", "beauty", price: 50m, salePrice: null);
        var service = new OrdersService(fixture.DbContext, TimeProvider.System);

        await Assert.ThrowsAsync<AppValidationException>(() => service.CreateAsync(
            Guid.NewGuid(),
            "key",
            CreateRequest("beauty-1", "beauty", clientUnitPrice: 50m, quantity: quantity),
            CancellationToken.None));
    }

    private static CreateOrderRequest CreateRequest(string productId, string groupId, decimal clientUnitPrice, int quantity) => new(
        [new OrderItemRequest(productId, groupId, "Client-supplied name", "/client.jpg", clientUnitPrice, quantity)],
        new DeliveryAddress("Test Customer", "customer@example.test", "1 Test Street", "London", "SW1A 1AA", "United Kingdom"),
        "standard",
        new PaymentSummary("tok_test_only", "Visa", "4242"),
        clientUnitPrice * quantity,
        0,
        clientUnitPrice * quantity);

    private static void SeedProduct(AppDbContext dbContext, string id, string groupId, decimal price, decimal? salePrice)
    {
        dbContext.ProductGroups.Add(new ProductGroup
        {
            Id = groupId,
            Name = groupId,
            Description = groupId,
            ImageUrl = "/group.jpg",
        });
        dbContext.Products.Add(new Product
        {
            Id = id,
            GroupId = groupId,
            Name = "Test product",
            Brand = "Test brand",
            Description = "Test description",
            ImageUrl = "/product.jpg",
            Price = price,
            SalePrice = salePrice,
            InStock = true,
        });
        dbContext.SaveChanges();
    }
}