namespace ShoppyShop.Application;

public sealed record ProductGroupDto(
    string Id,
    string Name,
    string Description,
    string ImageUrl,
    int ItemCount,
    string? Badge);

public sealed record ProductDto(
    string Id,
    string GroupId,
    string Name,
    string Brand,
    string Description,
    string ImageUrl,
    decimal Price,
    decimal? SalePrice,
    bool InStock);

public sealed record ProductQuery(
    string? Search = null,
    string? Sort = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    bool IncludeDeleted = false);

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record UserDto(Guid Id, string Email, string? DisplayName, IReadOnlyCollection<string> Roles);
public sealed record AuthResult(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, UserDto User);

public sealed record DeliveryAddress(
    string Name,
    string Email,
    string Address,
    string City,
    string Postcode,
    string Country);

public sealed record PaymentSummary(string TokenId, string Brand, string Last4);
public sealed record OrderItemRequest(
    string ProductId,
    string GroupId,
    string Name,
    string ImageUrl,
    decimal UnitPrice,
    int Quantity);
public sealed record CreateOrderRequest(
    IReadOnlyCollection<OrderItemRequest> Lines,
    DeliveryAddress Delivery,
    string DeliveryMethod,
    PaymentSummary PaymentToken,
    decimal Subtotal,
    decimal DeliveryCharge,
    decimal Total);

public sealed record OrderLineDto(
    string ProductId,
    string GroupId,
    string Name,
    string ImageUrl,
    decimal UnitPrice,
    int Quantity);

public sealed record OrderDto(
    string Id,
    DateTimeOffset CreatedAt,
    string Status,
    IReadOnlyCollection<OrderLineDto> Lines,
    DeliveryAddress Delivery,
    string DeliveryMethod,
    PaymentSummary PaymentToken,
    decimal Subtotal,
    decimal DeliveryCharge,
    decimal Total);

public sealed record ProductGroupWriteRequest(
    string Id,
    string Name,
    string Description,
    string ImageUrl,
    string? Badge,
    int DisplayOrder);

public sealed record ProductWriteRequest(
    string Id,
    string GroupId,
    string Name,
    string Brand,
    string Description,
    string ImageUrl,
    decimal Price,
    decimal? SalePrice,
    bool InStock);

public sealed record FeatureConfigDto(bool AssistantEnabled);

public sealed record AssistantChatTurn(string Role, string Content);
public sealed record AssistantChatRequest(string Message, IReadOnlyList<AssistantChatTurn>? History = null);
public sealed record AssistantChatResponse(string Reply, IReadOnlyCollection<ProductDto> Products);