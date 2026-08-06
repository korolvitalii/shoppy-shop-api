namespace ShoppyShop.Application;

public interface ICatalogueService
{
    Task<IReadOnlyCollection<ProductGroupDto>> GetGroupsAsync(bool includeDeleted, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProductDto>> GetProductsAsync(ProductQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProductDto>> GetGroupProductsAsync(string groupId, ProductQuery query, CancellationToken cancellationToken);
    Task<ProductDto?> GetProductAsync(string groupId, string productId, bool includeDeleted, CancellationToken cancellationToken);
}

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken);
    Task<UserDto?> GetUserAsync(Guid userId, CancellationToken cancellationToken);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken);
}

public interface IFavoritesService
{
    Task<IReadOnlyCollection<ProductDto>> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task AddAsync(Guid userId, string productId, CancellationToken cancellationToken);
    Task RemoveAsync(Guid userId, string productId, CancellationToken cancellationToken);
    Task ClearAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IOrdersService
{
    Task<IReadOnlyCollection<OrderDto>> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task<OrderDto?> GetAsync(Guid userId, string orderId, CancellationToken cancellationToken);
    Task<OrderDto> CreateAsync(Guid userId, string idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken);
}

public interface IAdminCatalogueService
{
    Task<ProductGroupDto> UpsertGroupAsync(string id, ProductGroupWriteRequest request, CancellationToken cancellationToken);
    Task DeleteGroupAsync(string id, CancellationToken cancellationToken);
    Task<ProductDto> UpsertProductAsync(string id, ProductWriteRequest request, CancellationToken cancellationToken);
    Task DeleteProductAsync(string id, CancellationToken cancellationToken);
}

public interface IAssistantService
{
    Task<AssistantChatResponse> ChatAsync(AssistantChatRequest request, CancellationToken cancellationToken);
}

public sealed class AppValidationException(string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}

public sealed class AppConflictException(string message) : Exception(message);
public sealed class AppNotFoundException(string message) : Exception(message);
public sealed class AppUnauthorizedException(string message) : Exception(message);
public sealed class AppUnprocessableException(string message) : Exception(message);