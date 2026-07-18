namespace ShoppyShop.Domain;

public sealed class Favorite
{
    public Guid UserId { get; set; }
    public required string ProductId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Product? Product { get; set; }
}

public sealed class Order
{
    public Guid Id { get; set; }
    public long OrderNumber { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DeliveryCharge { get; set; }
    public decimal Total { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public required string DeliveryName { get; set; }
    public required string DeliveryEmail { get; set; }
    public required string DeliveryAddressLine1 { get; set; }
    public required string DeliveryCity { get; set; }
    public required string DeliveryPostcode { get; set; }
    public required string DeliveryCountry { get; set; }
    public required string DeliveryMethod { get; set; }
    public required string PaymentTokenId { get; set; }
    public required string PaymentBrand { get; set; }
    public required string PaymentLast4 { get; set; }
    public List<OrderLine> Lines { get; } = [];
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public required string ProductId { get; set; }
    public required string GroupId { get; set; }
    public required string ProductName { get; set; }
    public required string ImageUrl { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public Order? Order { get; set; }
}

public sealed class OrderRequest
{
    public Guid UserId { get; set; }
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public Guid OrderId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Order? Order { get; set; }
}

public sealed class RefreshSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}