using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class CatalogueService(AppDbContext dbContext) : ICatalogueService
{
    private const int DefaultLimit = 24;
    private const int MaxLimit = 100;

    public async Task<IReadOnlyCollection<ProductGroupDto>> GetGroupsAsync(bool includeDeleted, CancellationToken cancellationToken)
    {
        var groups = includeDeleted
            ? dbContext.ProductGroups.IgnoreQueryFilters()
            : dbContext.ProductGroups;

        return await groups.AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new ProductGroupDto(
                x.Id,
                x.Name,
                x.Description,
                x.ImageUrl,
                x.Products.Count(product => !product.IsDeleted),
                x.Badge))
            .ToArrayAsync(cancellationToken);
    }

    public Task<ProductPageDto> GetProductsAsync(ProductQuery query, CancellationToken cancellationToken) =>
        ExecuteProductQueryAsync(query, null, cancellationToken);

    public Task<ProductPageDto> GetGroupProductsAsync(
        string groupId,
        ProductQuery query,
        CancellationToken cancellationToken) => ExecuteProductQueryAsync(query, groupId, cancellationToken);

    public async Task<ProductDto?> GetProductAsync(
        string groupId,
        string productId,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        var products = includeDeleted ? dbContext.Products.IgnoreQueryFilters() : dbContext.Products;
        return await products.AsNoTracking()
            .Where(x => x.GroupId == groupId && x.Id == productId)
            .Select(ProductProjection)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ProductPageDto> ExecuteProductQueryAsync(
        ProductQuery query,
        string? groupId,
        CancellationToken cancellationToken)
    {
        var sort = ProductSorts.Normalize(query.Sort);
        var limit = ResolveLimit(query.Limit);

        var products = SeekAfter(sort, ProductCursor.Decode(query.Cursor, sort));
        if (query.IncludeDeleted)
        {
            products = products.IgnoreQueryFilters();
        }

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            products = products.Where(x => x.GroupId == groupId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var terms = Regex.Split(query.Search.Trim(), @"[^\p{L}\p{N}]+")
                .Where(term => term.Length > 0)
                .Select(NormalizeSearchTerm)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var term in terms)
            {
                var pattern = $"%{term}%";
                products = dbContext.Database.IsNpgsql()
                    ? products.Where(x =>
                        EF.Functions.ILike(x.Name, pattern) ||
                        EF.Functions.ILike(x.Brand, pattern) ||
                        EF.Functions.ILike(x.Description, pattern))
                    : products.Where(x =>
                        EF.Functions.Like(x.Name, pattern) ||
                        EF.Functions.Like(x.Brand, pattern) ||
                        EF.Functions.Like(x.Description, pattern));
            }
        }

        if (query.MinPrice is not null)
        {
            products = products.Where(x => (x.SalePrice ?? x.Price) >= query.MinPrice);
        }

        if (query.MaxPrice is not null)
        {
            products = products.Where(x => (x.SalePrice ?? x.Price) <= query.MaxPrice);
        }

        products = ApplyOrder(products, sort);

        // Fetching one row past the page is how we learn whether a further page exists. The
        // alternative — a COUNT over the whole filtered set — would scan far more rows than the
        // page itself, and the client only needs to know "is there more", not how much more.
        var rows = await products.AsNoTracking()
            .Take(limit + 1)
            .Select(ProductProjection)
            .ToArrayAsync(cancellationToken);

        if (rows.Length <= limit)
        {
            return new ProductPageDto(rows, null);
        }

        var items = rows[..limit];
        var last = items[^1];
        return new ProductPageDto(items, new ProductCursor(sort, SortKey(sort, last), last.Id).Encode());
    }

    /// <summary>
    /// Starts the query at the row after <paramref name="cursor"/>, as a row-value comparison.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate is written as SQL rather than LINQ because the shape matters enormously and EF
    /// Core has no LINQ form for row values. <c>(key, "Id") &gt; (@key, @id)</c> is a single range
    /// condition PostgreSQL seeks straight to; the logically identical
    /// <c>key &gt; @key OR (key = @key AND "Id" &gt; @id)</c> that LINQ would produce is not — the
    /// planner degrades it to a filter and rescans the index from the beginning on every page.
    /// Measured on 300k rows, reading a page from the middle of the catalogue: 90ms for the OR form
    /// (239,999 rows discarded by the filter) against 0.2ms for this one, and that gap widens the
    /// deeper the page is. Getting this wrong costs the entire benefit of paging by cursor.
    /// </para>
    /// <para>
    /// Values are interpolated as parameters by <c>FromSql</c>, never as literals, and the rest of
    /// the query composes on top: EF wraps this as a subquery, which the planner flattens back into
    /// the same index scan.
    /// </para>
    /// </remarks>
    private IQueryable<Product> SeekAfter(string sort, ProductCursor? cursor)
    {
        if (cursor is null)
        {
            return dbContext.Products;
        }

        var id = cursor.Id;
        return sort switch
        {
            ProductSorts.PriceAscending => dbContext.Products.FromSql(
                $"""SELECT * FROM "Products" WHERE (COALESCE("SalePrice", "Price"), "Id") > ({ParsePrice(cursor)}, {id})"""),
            ProductSorts.PriceDescending => dbContext.Products.FromSql(
                $"""SELECT * FROM "Products" WHERE (COALESCE("SalePrice", "Price"), "Id") < ({ParsePrice(cursor)}, {id})"""),
            ProductSorts.Name => dbContext.Products.FromSql(
                $"""SELECT * FROM "Products" WHERE ("Name", "Id") > ({cursor.Key}, {id})"""),
            _ => dbContext.Products.FromSql(
                $"""SELECT * FROM "Products" WHERE ("GroupId", "Id") > ({cursor.Key}, {id})"""),
        };
    }

    /// <summary>
    /// The ORDER BY matching <see cref="SeekAfter"/>. The two must agree exactly, tiebreak included:
    /// where a page boundary falls inside a run of rows sharing a name or price, a mismatch silently
    /// drops or repeats them.
    /// </summary>
    private static IQueryable<Product> ApplyOrder(IQueryable<Product> products, string sort) => sort switch
    {
        ProductSorts.PriceAscending => products.OrderBy(x => x.SalePrice ?? x.Price).ThenBy(x => x.Id),

        // The tiebreak descends with the key rather than staying ascending, so this order is just the
        // ascending index read backwards. Mixing the directions would still be correct but would need
        // a second index built the other way round to stay seekable.
        ProductSorts.PriceDescending => products.OrderByDescending(x => x.SalePrice ?? x.Price).ThenByDescending(x => x.Id),

        ProductSorts.Name => products.OrderBy(x => x.Name).ThenBy(x => x.Id),
        _ => products.OrderBy(x => x.GroupId).ThenBy(x => x.Id),
    };

    private static string SortKey(string sort, ProductDto product) => sort switch
    {
        ProductSorts.PriceAscending or ProductSorts.PriceDescending =>
            (product.SalePrice ?? product.Price).ToString(CultureInfo.InvariantCulture),
        ProductSorts.Name => product.Name,
        _ => product.GroupId,
    };

    private static decimal ParsePrice(ProductCursor cursor) =>
        decimal.TryParse(cursor.Key, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            ? price
            : throw Invalid("cursor", "Pagination cursor is not valid.");

    private static int ResolveLimit(int? limit)
    {
        if (limit is null)
        {
            return DefaultLimit;
        }

        return limit is >= 1 and <= MaxLimit
            ? limit.Value
            : throw Invalid("limit", $"Page size must be between 1 and {MaxLimit}.");
    }

    private static AppValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, string[]> { [field] = [message] });

    private static string NormalizeSearchTerm(string term) =>
        term.Length > 3 && term.EndsWith('s') ? term[..^1] : term;

    private static System.Linq.Expressions.Expression<Func<Product, ProductDto>> ProductProjection => product =>
        new ProductDto(
            product.Id,
            product.GroupId,
            product.Name,
            product.Brand,
            product.Description,
            product.ImageUrl,
            product.Price,
            product.SalePrice,
            product.InStock);
}