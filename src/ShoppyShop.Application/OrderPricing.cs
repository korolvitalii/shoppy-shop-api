namespace ShoppyShop.Application;

public readonly record struct OrderTotals(decimal Subtotal, decimal DeliveryCharge, decimal Total);

/// <summary>
/// How an order is priced from stored catalogue prices. Client-submitted prices never reach this:
/// <c>OrdersService</c> passes the prices it read from the database.
/// </summary>
public static class OrderPricing
{
    /// <summary>The price a customer pays for one unit: the sale price when there is one.</summary>
    public static decimal UnitPrice(decimal price, decimal? salePrice) => salePrice ?? price;

    public static OrderTotals Price(IEnumerable<(decimal UnitPrice, int Quantity)> lines)
    {
        var subtotal = lines.Sum(line => line.UnitPrice * line.Quantity);
        var total = subtotal + CommerceLimits.DeliveryCharge;

        // A business ceiling, not a storage one: numeric(12,2) holds far more than this, so an
        // order overrunning the column is not the failure being prevented. What this catches is a
        // basket whose combined value is implausible for this shop.
        if (total > CommerceLimits.MaxOrderTotal)
        {
            throw new AppUnprocessableException($"Order total exceeds the maximum of {CommerceLimits.MaxOrderTotal:0.00}.");
        }

        return new OrderTotals(subtotal, CommerceLimits.DeliveryCharge, total);
    }
}