using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class OrdersService(AppDbContext dbContext, TimeProvider timeProvider) : IOrdersService
{
    private const int MaxOrderLines = CommerceLimits.MaxOrderLines;
    private const int MaxQuantityPerProduct = CommerceLimits.MaxQuantityPerProduct;
    private const decimal MaxOrderTotal = CommerceLimits.MaxOrderTotal;

    public async Task<IReadOnlyCollection<OrderDto>> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        (await dbContext.Orders.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToArrayAsync(cancellationToken))
        .Select(MapOrder)
        .ToArray();

    public async Task<OrderDto?> GetAsync(Guid userId, string orderId, CancellationToken cancellationToken)
    {
        if (!TryParseOrderNumber(orderId, out var orderNumber))
        {
            return null;
        }

        var order = await dbContext.Orders.AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.OrderNumber == orderNumber, cancellationToken);
        return order is null ? null : MapOrder(order);
    }

    public async Task<OrderDto> CreateAsync(
        Guid userId,
        string idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var quantities = Validate(request, idempotencyKey);
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

        // Serializable isolation does not block: Postgres aborts one side of a concurrent
        // order with SQLSTATE 40001 and expects the caller to retry. The execution strategy
        // supplies that retry, but it re-runs this delegate from the top, so the change
        // tracker is cleared first to drop entities a failed attempt left behind.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            return await CreateOrderAsync(userId, idempotencyKey, request, quantities, requestHash, ct);
        }, cancellationToken);
    }

    private async Task<OrderDto> CreateOrderAsync(
        Guid userId,
        string idempotencyKey,
        CreateOrderRequest request,
        IReadOnlyDictionary<string, int> quantities,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var previous = await dbContext.OrderRequests.IgnoreQueryFilters()
            .Include(x => x.Order!).ThenInclude(x => x.Lines)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.Key == idempotencyKey, cancellationToken);

        if (previous is not null && previous.ExpiresAt > now)
        {
            return ReplayOrder(previous, requestHash);
        }

        if (previous is not null)
        {
            dbContext.OrderRequests.Remove(previous);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var productIds = quantities.Keys.ToArray();
        var products = await dbContext.Products.AsNoTracking()
            .Where(x => productIds.Contains(x.Id))
            .ToArrayAsync(cancellationToken);
        if (products.Length != productIds.Length)
        {
            throw new AppUnprocessableException("One or more products do not exist.");
        }

        if (products.Any(x => !x.InStock))
        {
            throw new AppConflictException("One or more products are out of stock.");
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            Currency = "GBP",
            Status = "confirmed",
            DeliveryName = request.Delivery.Name.Trim(),
            DeliveryEmail = request.Delivery.Email.Trim(),
            DeliveryAddressLine1 = request.Delivery.Address.Trim(),
            DeliveryCity = request.Delivery.City.Trim(),
            DeliveryPostcode = request.Delivery.Postcode.Trim(),
            DeliveryCountry = request.Delivery.Country.Trim(),
            DeliveryMethod = "standard",
            PaymentTokenId = request.PaymentToken.TokenId.Trim(),
            PaymentBrand = request.PaymentToken.Brand.Trim(),
            PaymentLast4 = request.PaymentToken.Last4.Trim(),
        };

        foreach (var product in products)
        {
            var price = product.SalePrice ?? product.Price;
            order.Lines.Add(new OrderLine
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                GroupId = product.GroupId,
                ProductName = product.Name,
                ImageUrl = product.ImageUrl,
                UnitPrice = price,
                Quantity = quantities[product.Id],
            });
        }

        order.Subtotal = order.Lines.Sum(x => x.UnitPrice * x.Quantity);
        order.DeliveryCharge = CommerceLimits.DeliveryCharge;
        order.Total = order.Subtotal + order.DeliveryCharge;

        // A business ceiling, not a storage one: numeric(12,2) holds far more than this, so an
        // order overrunning the column is not the failure being prevented. What this catches is a
        // basket whose combined value is implausible for this shop.
        if (order.Total > MaxOrderTotal)
        {
            throw new AppUnprocessableException($"Order total exceeds the maximum of {MaxOrderTotal:0.00}.");
        }

        dbContext.Orders.Add(order);
        dbContext.OrderRequests.Add(new OrderRequest
        {
            UserId = userId,
            Key = idempotencyKey,
            RequestHash = requestHash,
            OrderId = order.Id,
            ExpiresAt = now.AddHours(24),
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request claimed this idempotency key first and won the race on
            // the OrderRequests primary key. Its order is the canonical result for this key,
            // so replay that rather than surfacing a unique-violation as a 500.
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.OrderRequests.AsNoTracking().IgnoreQueryFilters()
                .Include(x => x.Order!).ThenInclude(x => x.Lines)
                .SingleOrDefaultAsync(x => x.UserId == userId && x.Key == idempotencyKey, cancellationToken);

            if (winner?.Order is null)
            {
                throw;
            }

            return ReplayOrder(winner, requestHash);
        }

        await transaction.CommitAsync(cancellationToken);
        return MapOrder(order);
    }

    private static OrderDto ReplayOrder(OrderRequest previous, string requestHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(previous.RequestHash),
            Encoding.ASCII.GetBytes(requestHash)))
        {
            throw new AppConflictException("This idempotency key was already used with a different order.");
        }

        return MapOrder(previous.Order!);
    }

    /// <summary>
    /// Validates the request and returns the per-product quantities the order will be built from.
    /// Grouping happens here, before the transaction, because the quantity bound is only meaningful
    /// once duplicate lines for one product have been summed.
    /// </summary>
    private static Dictionary<string, int> Validate(CreateOrderRequest request, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
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

        if (request.Lines.Count == 0 || request.Lines.Count > MaxOrderLines)
        {
            throw new AppValidationException($"An order requires between 1 and {MaxOrderLines} lines.");
        }

        if (request.Lines.Any(x =>
            x is null || string.IsNullOrWhiteSpace(x.ProductId) || x.ProductId.Length > 100 ||
            x.Quantity is < 1 or > MaxQuantityPerProduct))
        {
            throw new AppValidationException(
                $"An order requires products with quantities between 1 and {MaxQuantityPerProduct}.");
        }

        var quantities = request.Lines
            .GroupBy(x => x.ProductId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.Quantity), StringComparer.Ordinal);

        // Two lines of 99 for the same product each satisfy the per-line bound and then become a
        // single line of 198. The bound belongs after the grouping, not before it.
        if (quantities.Values.Any(quantity => quantity > MaxQuantityPerProduct))
        {
            throw new AppValidationException(
                $"An order allows at most {MaxQuantityPerProduct} of any one product.");
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

    private static OrderDto MapOrder(Order order) => new(
        $"ORD-{order.OrderNumber:00000}",
        order.CreatedAt,
        order.Status,
        order.Lines.Select(x => new OrderLineDto(
            x.ProductId,
            x.GroupId,
            x.ProductName,
            x.ImageUrl,
            x.UnitPrice,
            x.Quantity)).ToArray(),
        new DeliveryAddress(
            order.DeliveryName,
            order.DeliveryEmail,
            order.DeliveryAddressLine1,
            order.DeliveryCity,
            order.DeliveryPostcode,
            order.DeliveryCountry),
        order.DeliveryMethod,
        new PaymentSummary(order.PaymentTokenId, order.PaymentBrand, order.PaymentLast4),
        order.Subtotal,
        order.DeliveryCharge,
        order.Total);

    private static bool TryParseOrderNumber(string value, out long orderNumber)
    {
        orderNumber = 0;
        return value.StartsWith("ORD-", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value.AsSpan(4), out orderNumber);
    }
}