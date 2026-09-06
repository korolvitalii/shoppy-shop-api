namespace ShoppyShop.Application;

/// <summary>
/// The catalogue sort values the Angular storefront sends. Every sort is completed with an ascending
/// <c>Id</c> tiebreak so the ordering is total: keyset pagination needs a key no two rows can share,
/// otherwise rows tied on name or price would be skipped or repeated across a page boundary.
/// </summary>
public static class ProductSorts
{
    public const string Featured = "featured";
    public const string PriceAscending = "price-asc";
    public const string PriceDescending = "price-desc";
    public const string Name = "name";

    public static string Normalize(string? sort) =>
        sort?.ToLowerInvariant() switch
        {
            PriceAscending => PriceAscending,
            PriceDescending => PriceDescending,
            Name => Name,
            _ => Featured,
        };
}