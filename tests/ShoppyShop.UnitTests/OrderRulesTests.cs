using ShoppyShop.Application;

namespace ShoppyShop.UnitTests;

/// <summary>
/// The checkout rules exercised directly, with no database: that they are reachable this way at all
/// is the point of keeping them in Application. <c>OrdersServiceTests</c> covers them end to end.
/// </summary>
public sealed class OrderRulesTests
{
    [Fact]
    public void ValidateSumsDuplicateLinesPerProduct()
    {
        var quantities = CreateOrderValidator.Validate(Request(("a", 2), ("b", 1), ("a", 3)), "key-1");

        Assert.Equal(5, quantities["a"]);
        Assert.Equal(1, quantities["b"]);
    }

    [Fact]
    public void ValidateAppliesTheQuantityBoundAfterGrouping()
    {
        // Each line is within the per-line bound; together they are not.
        Assert.Throws<AppValidationException>(() =>
            CreateOrderValidator.Validate(Request(("a", 99), ("a", 1)), "key-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateRejectsAMissingIdempotencyKey(string key)
    {
        Assert.Throws<AppValidationException>(() => CreateOrderValidator.Validate(Request(("a", 1)), key));
    }

    [Fact]
    public void ValidateRejectsAnOverlongIdempotencyKey()
    {
        Assert.Throws<AppValidationException>(() =>
            CreateOrderValidator.Validate(Request(("a", 1)), new string('k', 201)));
    }

    [Fact]
    public void ValidateRejectsNullMembersTheJsonBinderCanProduce()
    {
        var request = Request(("a", 1)) with { Delivery = null! };

        Assert.Throws<AppValidationException>(() => CreateOrderValidator.Validate(request, "key-1"));
    }

    [Theory]
    [InlineData("customer.example.test")]
    [InlineData("@example.test")]
    [InlineData("customer@")]
    [InlineData("a@b@c")]
    [InlineData("cust omer@example.test")]
    public void ValidateRejectsMalformedDeliveryEmails(string email)
    {
        var request = Request(("a", 1));
        request = request with { Delivery = request.Delivery with { Email = email } };

        Assert.Throws<AppValidationException>(() => CreateOrderValidator.Validate(request, "key-1"));
    }

    [Theory]
    [InlineData("424")]
    [InlineData("42a2")]
    public void ValidateRejectsAMalformedLastFour(string last4)
    {
        var request = Request(("a", 1));
        request = request with { PaymentToken = request.PaymentToken with { Last4 = last4 } };

        Assert.Throws<AppValidationException>(() => CreateOrderValidator.Validate(request, "key-1"));
    }

    [Fact]
    public void UnitPricePrefersTheSalePrice()
    {
        Assert.Equal(99.99m, OrderPricing.UnitPrice(120m, 99.99m));
        Assert.Equal(120m, OrderPricing.UnitPrice(120m, null));
    }

    [Fact]
    public void PriceAddsTheFlatDeliveryCharge()
    {
        var totals = OrderPricing.Price([(99.99m, 2), (10m, 1)]);

        Assert.Equal(209.98m, totals.Subtotal);
        Assert.Equal(CommerceLimits.DeliveryCharge, totals.DeliveryCharge);
        Assert.Equal(209.98m + CommerceLimits.DeliveryCharge, totals.Total);
    }

    [Fact]
    public void PriceAcceptsExactlyTheMaximumOrderTotal()
    {
        var totals = OrderPricing.Price([(CommerceLimits.MaxProductPrice, CommerceLimits.MaxQuantityPerProduct)]);

        Assert.Equal(CommerceLimits.MaxOrderTotal, totals.Total);
    }

    [Fact]
    public void PriceRejectsATotalAboveTheMaximum()
    {
        Assert.Throws<AppUnprocessableException>(() => OrderPricing.Price(
        [
            (CommerceLimits.MaxProductPrice, CommerceLimits.MaxQuantityPerProduct),
            (0.01m, 1),
        ]));
    }

    private static CreateOrderRequest Request(params (string ProductId, int Quantity)[] lines) => new(
        lines.Select(x => new OrderItemRequest(x.ProductId, "group", "Name", "/i.jpg", 1m, x.Quantity)).ToArray(),
        new DeliveryAddress("Test Customer", "customer@example.test", "1 Test Street", "London", "SW1A 1AA", "United Kingdom"),
        "standard",
        new PaymentSummary("tok_test_only", "Visa", "4242"),
        0,
        0,
        0);
}