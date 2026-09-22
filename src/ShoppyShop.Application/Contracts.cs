using System.ComponentModel.DataAnnotations;

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
    bool InStock,
    bool IsNew,
    bool GiftWrappable);

public sealed record ProductQuery(
    string? Search = null,
    string? Sort = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    bool? InStock = null,
    bool? IsNew = null,
    bool? GiftWrappable = null,
    bool IncludeDeleted = false,
    string? Cursor = null,
    int? Limit = null);

/// <summary>
/// One page of a product listing. <paramref name="NextCursor"/> is null on the last page — that is
/// the only end-of-list signal, because a keyset query never learns the total row count (deliberately:
/// a COUNT over the whole filtered set would cost more than the page itself). <paramref name="TotalCount"/>
/// is the one deliberate exception: it is only populated when <see cref="ProductQuery.Cursor"/> is null,
/// i.e. once per filter change rather than once per page, so the UI can show "Showing X of Y" without
/// paying for a COUNT on every "load more".
/// </summary>
public sealed record ProductPageDto(
    IReadOnlyCollection<ProductDto> Items,
    string? NextCursor,
    int? TotalCount = null);

// Annotated so the contract is enforced at the boundary and visible in OpenAPI, rather than being
// discoverable only by reading each service. A non-nullable `string` here is a declaration, not a
// runtime guarantee: the JSON binder still yields null for an absent or explicitly null member, and
// Identity passes some of these straight to the password hasher, which throws on null. The
// hand-rolled checks inside the services stay as defence in depth - they carry the tested messages,
// and they are what protects a service called directly rather than through an endpoint.
public sealed record RegisterRequest(
    [property: Required, StringLength(320)] string Email,
    [property: Required] string Password,
    [property: StringLength(200)] string? DisplayName);
public sealed record LoginRequest(
    [property: Required, StringLength(320)] string Email,
    [property: Required] string Password);
public sealed record ChangePasswordRequest(
    [property: Required] string CurrentPassword,
    [property: Required] string NewPassword);
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
    bool InStock,
    bool IsNew = false,
    bool GiftWrappable = false);

public sealed record FeatureConfigDto(bool AssistantEnabled);

public sealed record AssistantChatTurn(string Role, string Content);
public sealed record AssistantChatRequest(string Message, IReadOnlyList<AssistantChatTurn>? History = null);
public sealed record AssistantChatResponse(string Reply, IReadOnlyCollection<ProductDto> Products);