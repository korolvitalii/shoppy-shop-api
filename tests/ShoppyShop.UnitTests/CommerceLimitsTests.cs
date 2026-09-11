using ShoppyShop.Application;

namespace ShoppyShop.UnitTests;

public sealed class CommerceLimitsTests
{
    [Fact]
    public void TheDearestListableProductIsStillOrderableAtTheMaximumQuantity()
    {
        // The failure this prevents: an admin lists a product the catalogue accepts, and every
        // attempt to buy it is rejected at checkout with no path forward for the customer. The two
        // ceilings were briefly set independently (1,000,000 and 100,000) and did exactly that to
        // anything priced above 99,995.01.
        var dearestPossibleOrder =
            (CommerceLimits.MaxProductPrice * CommerceLimits.MaxQuantityPerProduct) + CommerceLimits.DeliveryCharge;

        Assert.True(
            CommerceLimits.MaxOrderTotal >= dearestPossibleOrder,
            $"MaxOrderTotal ({CommerceLimits.MaxOrderTotal}) must cover the dearest single-product " +
            $"order MaxProductPrice allows ({dearestPossibleOrder}), or that product is listable " +
            "but permanently un-orderable.");
    }

    [Fact]
    public void TheOrderTotalCeilingStillBoundsAMultiLineBasket()
    {
        // Derivation must not collapse into "no bound at all": a full basket can still exceed it.
        var dearestPossibleBasket =
            CommerceLimits.MaxProductPrice * CommerceLimits.MaxQuantityPerProduct * CommerceLimits.MaxOrderLines;

        Assert.True(CommerceLimits.MaxOrderTotal < dearestPossibleBasket);
    }
}