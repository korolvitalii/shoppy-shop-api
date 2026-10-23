using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed partial class CatalogueService(AppDbContext dbContext, CatalogueLocale? requestLocale = null) : ICatalogueService
{
    private string Locale => requestLocale?.Value ?? CatalogueLocale.LegacyDefault;
    private string? TranslationLocale => requestLocale is { IsSource: false } ? requestLocale.Value : null;
    private const int DefaultLimit = 24;
    private const int MaxLimit = 100;

    // Search is the one input on this endpoint whose cost the caller chooses. Every term adds an
    // ILIKE over three columns, and '%term%' leads with a wildcard so no btree index applies and
    // each one is a sequential scan. Unbounded, an anonymous caller could hand the planner a few
    // hundred predicates over a thousand-odd column comparisons and have them run twice - once for
    // the count below, once for the page. These two bounds are the cap that was missing; the long
    // term answer is a pg_trgm GIN index on Name/Brand, which makes ILIKE '%x%' indexable.
    internal const int MaxSearchLength = 120;
    private const int MaxSearchTerms = 6;

    public async Task<IReadOnlyCollection<ProductGroupDto>> GetGroupsAsync(bool includeDeleted, CancellationToken cancellationToken)
    {
        var groups = includeDeleted
            ? dbContext.ProductGroups.IgnoreQueryFilters()
            : dbContext.ProductGroups;

        var locale = Locale;
        return await groups.AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new ProductGroupDto(
                x.Id,
                x.Translations.Where(t => t.Locale == locale && t.SourceName == x.Name && t.SourceDescription == x.Description && t.SourceBadge == x.Badge).Select(t => t.Name).FirstOrDefault() ?? x.Name,
                x.Translations.Where(t => t.Locale == locale && t.SourceName == x.Name && t.SourceDescription == x.Description && t.SourceBadge == x.Badge).Select(t => t.Description).FirstOrDefault() ?? x.Description,
                x.ImageUrl,
                x.Products.Count(product => !product.IsDeleted),
                x.Translations.Where(t => t.Locale == locale && t.SourceName == x.Name && t.SourceDescription == x.Description && t.SourceBadge == x.Badge).Select(t => t.Badge).FirstOrDefault() ?? x.Badge))
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
            .Select(LocalizedProductProjection(TranslationLocale))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ProductPageDto> ExecuteProductQueryAsync(
        ProductQuery query,
        string? groupId,
        CancellationToken cancellationToken)
    {
        var sort = ProductSorts.Normalize(query.Sort);
        var limit = ResolveLimit(query.Limit);

        var cursor = ProductCursor.Decode(query.Cursor, sort, Locale);
        // A translated name sort seeks on the projected name (below), not an indexed column: every
        // row resolves its translation through a correlated subquery. Fine for the seeded catalogue;
        // at real size it needs a stored, indexed sort key per locale.
        var source = TranslationLocale is not null && sort == ProductSorts.Name ? dbContext.Products : SeekAfter(sort, cursor);
        if (query.IncludeDeleted)
        {
            source = source.IgnoreQueryFilters();
        }

        var products = source.Select(LocalizedProductProjection(TranslationLocale));

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            products = products.Where(x => x.GroupId == groupId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (query.Search.Length > MaxSearchLength)
            {
                throw Invalid("search", $"Search must be {MaxSearchLength} characters or fewer.");
            }

            var terms = SearchTermPattern().Split(query.Search.Trim())
                .Where(term => term.Length > 0)
                .Select(NormalizeSearchTerm)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxSearchTerms)
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

        if (query.InStock is true)
        {
            products = products.Where(x => x.InStock);
        }

        if (query.IsNew is true)
        {
            products = products.Where(x => x.IsNew);
        }

        if (query.GiftWrappable is true)
        {
            products = products.Where(x => x.GiftWrappable);
        }

        // The total is only worth paying for once per filter change, not once per page: a cursor
        // means this is a later page of a listing the client already counted, so skip it there.
        int? totalCount = query.Cursor is null
            ? await products.AsNoTracking().CountAsync(cancellationToken)
            : null;

        if (TranslationLocale is not null && sort == ProductSorts.Name && cursor is not null)
        {
            // EF translates these comparisons; they must use the database ORDER BY collation.
#pragma warning disable CA1309
            products = products.Where(x => string.Compare(x.Name, cursor.Key) > 0 ||
                (x.Name == cursor.Key && string.Compare(x.Id, cursor.Id) > 0));
#pragma warning restore CA1309
        }
        products = ApplyOrder(products, sort);

        // Fetching one row past the page is how we learn whether a further page exists. The
        // alternative — a COUNT over the whole filtered set — would scan far more rows than the
        // page itself, and the client only needs to know "is there more", not how much more.
        var rows = await products.AsNoTracking()
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);

        if (rows.Length <= limit)
        {
            return new ProductPageDto(rows, null, totalCount);
        }

        var items = rows[..limit];
        var last = items[^1];
        return new ProductPageDto(items, new ProductCursor(sort, SortKey(sort, last), last.Id, Locale).Encode(), totalCount);
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
    private static IQueryable<ProductDto> ApplyOrder(IQueryable<ProductDto> products, string sort) => sort switch
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
        new(message, new Dictionary<string, string[]> { [field] = [message] }, "catalogue." + field + "_invalid",
            [new ValidationIssue(field, "catalogue." + field + "_invalid", new Dictionary<string, object> { ["maximum"] = field == "search" ? MaxSearchLength : MaxLimit })]);

    /// <summary>
    /// Source-generated rather than a <see cref="Regex"/> literal: the static <c>Regex.Split</c>
    /// overload does cache its compiled pattern, so this is not about avoiding a recompile - it is
    /// about skipping the per-call cache lookup and the interpreted matcher, and matching how
    /// <c>AssistantService</c> already holds its pattern in a static field.
    /// </summary>
    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex SearchTermPattern();

    private static string NormalizeSearchTerm(string term) =>
        term.Length > 3 && term.EndsWith('s') ? term[..^1] : term;

    internal static System.Linq.Expressions.Expression<Func<Product, ProductDto>> LocalizedProductProjection(string? locale) => product =>
        new ProductDto
        {
            Id = product.Id,
            GroupId = product.GroupId,
            Name = locale == null ? product.Name : product.Translations.Where(t => t.Locale == locale && t.SourceName == product.Name && t.SourceDescription == product.Description).Select(t => t.Name).FirstOrDefault() ?? product.Name,
            Brand = product.Brand,
            Description = locale == null ? product.Description : product.Translations.Where(t => t.Locale == locale && t.SourceName == product.Name && t.SourceDescription == product.Description).Select(t => t.Description).FirstOrDefault() ?? product.Description,
            ImageUrl = product.ImageUrl,
            Price = product.Price,
            SalePrice = product.SalePrice,
            InStock = product.InStock,
            IsNew = product.IsNew,
            GiftWrappable = product.GiftWrappable,
        };
}