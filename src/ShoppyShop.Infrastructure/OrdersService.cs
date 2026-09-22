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
    private const int DefaultHistoryPageSize = 50;
    private const int MaxHistoryPageSize = 100;

    /// <summary>
    /// The newest <paramref name="limit"/> orders, starting from the one before the order identified
    /// by <paramref name="before"/> and <paramref name="beforeId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This collection grows for the life of an account and used to be returned whole, every line
    /// included, with no <c>Take</c> and no cap - so the one list here that grows per user was the
    /// one with no ceiling, while the catalogue got a keyset pager. The window seeks on the existing
    /// <c>(UserId, CreatedAt)</c> index from <c>Persistence.OnModelCreating</c>.
    /// </para>
    /// <para>
    /// The response stays a bare array rather than becoming a <c>{ items, nextCursor }</c> envelope:
    /// the Angular client reads it as <c>Order[]</c>, so bounding the page below anything it reaches
    /// keeps that contract intact. The cursor is a pair rather than the single <c>before</c> alone,
    /// though: <c>CreatedAt</c> is not unique, so a strict <c>&lt;</c> on it drops orders that share a
    /// boundary timestamp with the last item of the previous page. <paramref name="beforeId"/> is that
    /// last item's <c>OrderNumber</c> (as the same <c>"ORD-00000"</c> id the client already has on
    /// every returned order), and <see cref="SeekBefore"/> compares the pair as a row value so ties are
    /// broken deterministically instead of silently dropped.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyCollection<OrderDto>> GetAsync(
        Guid userId,
        DateTimeOffset? before,
        string? beforeId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var pageSize = ResolveHistoryPageSize(limit);
        var cursor = ResolveHistoryCursor(before, beforeId);

        var query = SeekBefore(cursor)
            .Where(x => x.UserId == userId)
            .Include(x => x.Lines)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.OrderNumber)
            .Take(pageSize);

        return (await query.ToArrayAsync(cancellationToken))
            .Select(MapOrder)
            .ToArray();
    }

    /// <summary>
    /// Starts the query at the row after <paramref name="cursor"/>, as a row-value comparison.
    /// </summary>
    /// <remarks>
    /// Same technique as <c>CatalogueService.SeekAfter</c>, for the same reason: the equivalent LINQ
    /// form, <c>CreatedAt &lt; cutoff || (CreatedAt == cutoff &amp;&amp; OrderNumber &lt; orderNumber)</c>,
    /// is logically identical but is not a single range condition, so Postgres cannot seek it against
    /// the <c>(UserId, CreatedAt)</c> index the way it can <c>("CreatedAt", "OrderNumber") &lt;
    /// (@cutoff, @orderNumber)</c>. <c>OrderNumber</c> descending is the tiebreak because the
    /// <c>ORDER BY</c> below matches it exactly - <c>ThenByDescending(x =&gt; x.OrderNumber)</c> - and
    /// the two must agree or a boundary shared between orders drops or repeats one of them.
    /// </remarks>
    private IQueryable<Order> SeekBefore(HistoryCursor? cursor)
    {
        if (cursor is null)
        {
            return dbContext.Orders.AsNoTracking();
        }

        return dbContext.Orders
            .FromSql($"""SELECT * FROM "Orders" WHERE ("CreatedAt", "OrderNumber") < ({cursor.Value.CreatedAt}, {cursor.Value.OrderNumber})""")
            .AsNoTracking();
    }

    private static HistoryCursor? ResolveHistoryCursor(DateTimeOffset? before, string? beforeId)
    {
        if (before is null && beforeId is null)
        {
            return null;
        }

        // Require the pair rather than accepting `before` alone: a lone timestamp cannot
        // disambiguate a tie (see SeekBefore), so accepting it would silently reintroduce the bug
        // this cursor exists to fix instead of rejecting the malformed request outright.
        if (before is null || beforeId is null || !TryParseOrderNumber(beforeId, out var orderNumber))
        {
            throw new AppValidationException(
                "before and beforeId must be supplied together, using the id of the last order from the previous page.");
        }

        return new HistoryCursor(before.Value, orderNumber);
    }

    private readonly record struct HistoryCursor(DateTimeOffset CreatedAt, long OrderNumber);

    private static int ResolveHistoryPageSize(int? limit)
    {
        if (limit is null)
        {
            return DefaultHistoryPageSize;
        }

        return limit is >= 1 and <= MaxHistoryPageSize
            ? limit.Value
            : throw new AppValidationException($"Page size must be between 1 and {MaxHistoryPageSize}.");
    }

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
        var quantities = CreateOrderValidator.Validate(request, idempotencyKey);
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
            order.Lines.Add(new OrderLine
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                GroupId = product.GroupId,
                ProductName = product.Name,
                ImageUrl = product.ImageUrl,
                UnitPrice = OrderPricing.UnitPrice(product.Price, product.SalePrice),
                Quantity = quantities[product.Id],
            });
        }

        (order.Subtotal, order.DeliveryCharge, order.Total) =
            OrderPricing.Price(order.Lines.Select(x => (x.UnitPrice, x.Quantity)));

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