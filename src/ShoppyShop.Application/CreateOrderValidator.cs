namespace ShoppyShop.Application;

/// <summary>
/// The checkout request rules, kept free of EF and I/O so they can be exercised without a database.
/// <c>OrdersService</c> calls this before it opens a transaction.
/// </summary>
public static class CreateOrderValidator
{
    private const int MaxIdempotencyKeyLength = 200;
    private const int MaxProductIdLength = 100;

    /// <summary>
    /// Validates the request and returns the per-product quantities the order will be built from.
    /// Grouping happens here, before the transaction, because the quantity bound is only meaningful
    /// once duplicate lines for one product have been summed.
    /// </summary>
    public static Dictionary<string, int> Validate(CreateOrderRequest request, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new AppValidationException("A valid Idempotency-Key header is required.");
        }

        // These members are declared non-nullable, but the JSON binder still yields null for an
        // absent or explicitly null member. Without these checks a malformed body dereferences null
        // and returns 500 instead of a validation error.
        if (request.Lines is null || request.Delivery is null || request.PaymentToken is null)
        {
            throw new AppValidationException("An order requires lines, a delivery address, and a payment token.");
        }

        if (request.Lines.Count == 0 || request.Lines.Count > CommerceLimits.MaxOrderLines)
        {
            throw new AppValidationException($"An order requires between 1 and {CommerceLimits.MaxOrderLines} lines.");
        }

        if (request.Lines.Any(x =>
            x is null || string.IsNullOrWhiteSpace(x.ProductId) || x.ProductId.Length > MaxProductIdLength ||
            x.Quantity is < 1 or > CommerceLimits.MaxQuantityPerProduct))
        {
            throw new AppValidationException(
                $"An order requires products with quantities between 1 and {CommerceLimits.MaxQuantityPerProduct}.");
        }

        var quantities = request.Lines
            .GroupBy(x => x.ProductId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.Quantity), StringComparer.Ordinal);

        // Two lines of 99 for the same product each satisfy the per-line bound and then become a
        // single line of 198. The bound belongs after the grouping, not before it.
        if (quantities.Values.Any(quantity => quantity > CommerceLimits.MaxQuantityPerProduct))
        {
            throw new AppValidationException(
                $"An order allows at most {CommerceLimits.MaxQuantityPerProduct} of any one product.");
        }

        ValidateDelivery(request.Delivery);

        if (!string.Equals(request.DeliveryMethod, "standard", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.PaymentToken.TokenId) || request.PaymentToken.TokenId.Length > 200 ||
            string.IsNullOrWhiteSpace(request.PaymentToken.Brand) || request.PaymentToken.Brand.Length > 40 ||
            request.PaymentToken.Last4?.Length != 4 ||
            !request.PaymentToken.Last4.All(char.IsDigit))
        {
            throw new AppValidationException("A valid payment token, brand, and last four digits are required.");
        }

        return quantities;
    }

    /// <summary>
    /// Lengths mirror the column widths configured in <c>Persistence.OnModelCreating</c>; a value
    /// that passes a presence check but exceeds its column becomes a database error at SaveChanges
    /// rather than a 400.
    /// </summary>
    private static void ValidateDelivery(DeliveryAddress delivery)
    {
        RequiredField(delivery.Name, 200, "Delivery name");
        RequiredField(delivery.Email, 320, "Delivery email");
        RequiredField(delivery.Address, 300, "Delivery address");
        RequiredField(delivery.City, 120, "Delivery city");
        RequiredField(delivery.Postcode, 30, "Delivery postcode");
        RequiredField(delivery.Country, 100, "Delivery country");

        var email = delivery.Email.Trim();
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == email.Length - 1 ||
            email.IndexOf('@', at + 1) >= 0 ||
            email.Any(char.IsWhiteSpace))
        {
            throw new AppValidationException("A valid delivery email address is required.");
        }
    }

    private static void RequiredField(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppValidationException($"{field} is required.");
        }

        if (value.Trim().Length > maxLength)
        {
            throw new AppValidationException($"{field} must be at most {maxLength} characters.");
        }
    }
}