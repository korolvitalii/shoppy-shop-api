namespace ShoppyShop.Application;

public sealed record ValidatedProductGroup(string Name, string Description, string ImageUrl, string? Badge);

public sealed record ValidatedProduct(string GroupId, string Name, string Brand, string Description, string ImageUrl);

/// <summary>
/// The admin catalogue write rules, kept free of EF and I/O. Checks that need the database - does
/// the product group exist - stay in <c>AdminCatalogueService</c>.
/// </summary>
public static class CatalogueWriteValidator
{
    // Prices are stored as numeric(12,2). PostgreSQL rounds anything finer to that scale without
    // complaint, so 0.004 would pass a ">0" check and persist as 0.00 — a free product at checkout,
    // which reads the stored price rather than the submitted one.
    private const decimal MaxPrice = CommerceLimits.MaxProductPrice;

    // Mirrors the HasMaxLength(...) calls in Persistence.cs. Kept in sync with the columns they
    // describe, so a value that clears these checks is guaranteed to fit without truncation.
    private const int MaxGroupIdLength = 80;
    private const int MaxGroupNameLength = 160;
    private const int MaxGroupImageUrlLength = 2_000;
    private const int MaxGroupBadgeLength = 80;
    private const int MaxProductIdLength = 100;
    private const int MaxProductGroupIdLength = 80;
    private const int MaxProductNameLength = 200;
    private const int MaxProductBrandLength = 160;
    private const int MaxProductImageUrlLength = 2_000;

    // Description is the one string column with no width in Persistence.OnModelCreating, so it maps
    // to an unbounded `text`. It is also one of the three columns the catalogue search ILIKEs over,
    // which makes it the most expensive of them to scan. Bound it on the way in rather than leaving
    // the only limit to what a client is willing to upload.
    private const int MaxDescriptionLength = 4_000;

    /// <summary>Validates a group write and returns its fields trimmed.</summary>
    public static ValidatedProductGroup ValidateGroup(string id, ProductGroupWriteRequest request)
    {
        ValidateId(id, request.Id, MaxGroupIdLength);
        return new ValidatedProductGroup(
            Required(request.Name, nameof(request.Name), MaxGroupNameLength),
            Required(request.Description, nameof(request.Description), MaxDescriptionLength),
            Required(request.ImageUrl, nameof(request.ImageUrl), MaxGroupImageUrlLength),
            OptionalWithMaxLength(request.Badge, nameof(request.Badge), MaxGroupBadgeLength));
    }

    /// <summary>Validates a product write and returns its string fields trimmed.</summary>
    public static ValidatedProduct ValidateProduct(string id, ProductWriteRequest request)
    {
        ValidateId(id, request.Id, MaxProductIdLength);
        ValidateMoney(request.Price, nameof(request.Price));
        if (request.SalePrice is { } salePrice)
        {
            ValidateMoney(salePrice, nameof(request.SalePrice));
        }

        if (request.SalePrice >= request.Price)
        {
            throw new AppUnprocessableException("Price must be positive and sale price must be lower than price.");
        }

        return new ValidatedProduct(
            Required(request.GroupId, nameof(request.GroupId), MaxProductGroupIdLength),
            Required(request.Name, nameof(request.Name), MaxProductNameLength),
            Required(request.Brand, nameof(request.Brand), MaxProductBrandLength),
            Required(request.Description, nameof(request.Description), MaxDescriptionLength),
            Required(request.ImageUrl, nameof(request.ImageUrl), MaxProductImageUrlLength));
    }

    private static void ValidateId(string routeId, string bodyId, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(routeId) || !string.Equals(routeId, bodyId, StringComparison.Ordinal))
        {
            throw new AppValidationException("Route and body identifiers must match.");
        }

        if (routeId.Length > maxLength)
        {
            throw new AppUnprocessableException($"Id must be {maxLength} characters or fewer.");
        }
    }

    private static void ValidateMoney(decimal value, string field)
    {
        if (decimal.Round(value, 2) != value)
        {
            throw new AppUnprocessableException($"{field} must have at most two decimal places.");
        }

        if (value <= 0 || value > MaxPrice)
        {
            throw new AppUnprocessableException($"{field} must be greater than zero and at most {MaxPrice:0.00}.");
        }
    }

    private static string Required(string value, string field, int? maxLength = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppValidationException($"{field} is required.");
        }

        var trimmed = value.Trim();
        if (maxLength is { } max && trimmed.Length > max)
        {
            throw new AppUnprocessableException($"{field} must be {max} characters or fewer.");
        }

        return trimmed;
    }

    private static string? OptionalWithMaxLength(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new AppUnprocessableException($"{field} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }
}