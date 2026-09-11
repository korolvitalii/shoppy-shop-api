namespace ShoppyShop.Application;

/// <summary>
/// The commerce bounds, in one place because they constrain each other. A price the catalogue
/// accepts must stay orderable: choosing a product ceiling and an order ceiling independently is
/// how a listable-but-permanently-un-orderable product appears, rejected at checkout with no path
/// forward for the customer. <see cref="MaxOrderTotal"/> is therefore derived, not picked.
/// </summary>
public static class CommerceLimits
{
    /// <summary>
    /// Ceiling for an admin-entered catalogue price. The seeded catalogue tops out near 420, so this
    /// is generous headroom rather than a tight fit — it exists to catch a mistyped price, not to
    /// express a merchandising rule.
    /// </summary>
    public const decimal MaxProductPrice = 10_000m;

    /// <summary>Per-product quantity in one order, applied after duplicate lines are summed.</summary>
    public const int MaxQuantityPerProduct = 99;

    /// <summary>Distinct products in one order.</summary>
    public const int MaxOrderLines = 100;

    /// <summary>The single flat delivery rate the shop currently charges.</summary>
    public const decimal DeliveryCharge = 4.99m;

    /// <summary>
    /// The largest order the API accepts. Set to exactly the dearest single-product order that
    /// <see cref="MaxProductPrice"/> allows, so no listable product is ever un-orderable. It still
    /// does real work: a full <see cref="MaxOrderLines"/> basket could otherwise reach a hundred
    /// times this. <c>CommerceLimitsTests</c> fails the build if the two are edited apart.
    /// </summary>
    public const decimal MaxOrderTotal = (MaxProductPrice * MaxQuantityPerProduct) + DeliveryCharge;
}