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
        Validate(request, idempotencyKey);
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

        // Serializable isolation does not block: Postgres aborts one side of a concurrent
        // order with SQLSTATE 40001 and expects the caller to retry. The execution strategy
        // supplies that retry, but it re-runs this delegate from the top, so the change
        // tracker is cleared first to drop entities a failed attempt left behind.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            return await CreateOrderAsync(userId, idempotencyKey, request, requestHash, ct);
        }, cancellationToken);
    }

    private async Task<OrderDto> CreateOrderAsync(
        Guid userId,
        string idempotencyKey,
        CreateOrderRequest request,
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

        var quantities = request.Lines
            .GroupBy(x => x.ProductId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.Quantity), StringComparer.Ordinal);
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
        order.DeliveryCharge = 4.99m;
        order.Total = order.Subtotal + order.DeliveryCharge;
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

    private static void Validate(CreateOrderRequest request, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new AppValidationException("A valid Idempotency-Key header is required.");
        }

        if (request.Lines.Count == 0 || request.Lines.Any(x => string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity is < 1 or > 99))
        {
            throw new AppValidationException("An order requires products with quantities between 1 and 99.");
        }

        if (string.IsNullOrWhiteSpace(request.Delivery.Name) ||
            string.IsNullOrWhiteSpace(request.Delivery.Email) ||
            string.IsNullOrWhiteSpace(request.Delivery.Address) ||
            string.IsNullOrWhiteSpace(request.Delivery.City) ||
            string.IsNullOrWhiteSpace(request.Delivery.Postcode) ||
            string.IsNullOrWhiteSpace(request.Delivery.Country))
        {
            throw new AppValidationException("A complete delivery address is required.");
        }

        if (!string.Equals(request.DeliveryMethod, "standard", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.PaymentToken.TokenId) ||
            string.IsNullOrWhiteSpace(request.PaymentToken.Brand) ||
            request.PaymentToken.Last4.Length != 4 ||
            !request.PaymentToken.Last4.All(char.IsDigit))
        {
            throw new AppValidationException("A valid payment token, brand, and last four digits are required.");
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