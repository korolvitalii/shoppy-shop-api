namespace ShoppyShop.Application;

public static class PricePresets
{
    public static (decimal? Min, decimal? Max) Resolve(string? price, decimal? min, decimal? max) =>
        price switch
        {
            "0-50" => (0, 49.99m),
            "50-200" => (50, 199.99m),
            "200+" => (200, null),
            _ => (min, max),
        };
}