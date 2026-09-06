using System.IdentityModel.Tokens.Jwt;

using Microsoft.Extensions.Options;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.Api;

public static class ApiEndpoints
{
    private const string RefreshCookie = "shoppy.refresh";

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapCatalogue(endpoints.MapGroup("/api").WithTags("Catalogue"));
        MapFeatureConfig(endpoints.MapGroup("/api").WithTags("Configuration"));
        MapAssistant(endpoints.MapGroup("/api/assistant").WithTags("Assistant").RequireRateLimiting("assistant"));
        MapAuth(endpoints.MapGroup("/api/auth").WithTags("Authentication"));
        MapFavorites(endpoints.MapGroup("/api/favorites").WithTags("Favorites").RequireAuthorization());
        MapOrders(endpoints.MapGroup("/api/orders").WithTags("Orders").RequireAuthorization());
        MapAdmin(endpoints.MapGroup("/api/admin").WithTags("Administration").RequireAuthorization(policy => policy.RequireRole("Admin")));
        return endpoints;
    }

    private static void MapCatalogue(RouteGroupBuilder api)
    {
        api.MapGet("/product-groups", (ICatalogueService service, CancellationToken cancellationToken) =>
            service.GetGroupsAsync(false, cancellationToken));

        api.MapGet("/products", (
            string? search,
            string? sort,
            string? price,
            decimal? minPrice,
            decimal? maxPrice,
            string? cursor,
            int? limit,
            ICatalogueService service,
            CancellationToken cancellationToken) =>
        {
            (minPrice, maxPrice) = PricePresets.Resolve(price, minPrice, maxPrice);
            return service.GetProductsAsync(
                new ProductQuery(search, sort, minPrice, maxPrice, Cursor: cursor, Limit: limit),
                cancellationToken);
        });

        api.MapGet("/product-groups/{groupId}/products", (
            string groupId,
            string? search,
            string? sort,
            string? price,
            decimal? minPrice,
            decimal? maxPrice,
            string? cursor,
            int? limit,
            ICatalogueService service,
            CancellationToken cancellationToken) =>
        {
            (minPrice, maxPrice) = PricePresets.Resolve(price, minPrice, maxPrice);
            return service.GetGroupProductsAsync(
                groupId,
                new ProductQuery(search, sort, minPrice, maxPrice, Cursor: cursor, Limit: limit),
                cancellationToken);
        });

        api.MapGet("/product-groups/{groupId}/products/{productId}", async (
            string groupId,
            string productId,
            ICatalogueService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.GetProductAsync(groupId, productId, false, cancellationToken);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });
    }

    private static void MapFeatureConfig(RouteGroupBuilder api)
    {
        api.MapGet("/feature-config", (IOptions<FeatureConfigOptions> options) =>
            new FeatureConfigDto(options.Value.AssistantEnabled));
    }

    private static void MapAssistant(RouteGroupBuilder assistant)
    {
        assistant.MapPost("/chat", (
            AssistantChatRequest request,
            IAssistantService service,
            CancellationToken cancellationToken) => service.ChatAsync(request, cancellationToken));
    }

    private static void MapAuth(RouteGroupBuilder auth)
    {
        auth.MapPost("/register", async (
            RegisterRequest request,
            IAuthService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            ToAuthResponse(context, await service.RegisterAsync(request, cancellationToken)))
            .RequireRateLimiting("auth");

        auth.MapPost("/login", async (
            LoginRequest request,
            IAuthService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            ToAuthResponse(context, await service.LoginAsync(request, cancellationToken)))
            .RequireRateLimiting("auth");

        auth.MapPost("/refresh", async (
            IAuthService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.Cookies.TryGetValue(RefreshCookie, out var refreshToken))
            {
                throw new AppUnauthorizedException("Refresh cookie is missing.");
            }

            return ToAuthResponse(context, await service.RefreshAsync(refreshToken, cancellationToken));
        }).RequireRateLimiting("auth");

        auth.MapPost("/logout", async (
            IAuthService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Request.Cookies.TryGetValue(RefreshCookie, out var refreshToken);
            await service.LogoutAsync(refreshToken, cancellationToken);
            context.Response.Cookies.Delete(RefreshCookie, RefreshCookieOptions());
            return Results.NoContent();
        });

        auth.MapGet("/me", async (IAuthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            var user = await service.GetUserAsync(context.User.UserId(), cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).RequireAuthorization();

        auth.MapPost("/change-password", async (
            ChangePasswordRequest request,
            IAuthService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            await service.ChangePasswordAsync(context.User.UserId(), request, cancellationToken);
            context.Response.Cookies.Delete(RefreshCookie, RefreshCookieOptions());
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static void MapFavorites(RouteGroupBuilder favorites)
    {
        favorites.MapGet("/", (IFavoritesService service, HttpContext context, CancellationToken cancellationToken) =>
            service.GetAsync(context.User.UserId(), cancellationToken));
        favorites.MapPut("/{productId}", async (
            string productId,
            IFavoritesService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            await service.AddAsync(context.User.UserId(), productId, cancellationToken);
            return Results.NoContent();
        });
        favorites.MapDelete("/{productId}", async (
            string productId,
            IFavoritesService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            await service.RemoveAsync(context.User.UserId(), productId, cancellationToken);
            return Results.NoContent();
        });
        favorites.MapDelete("/", async (IFavoritesService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            await service.ClearAsync(context.User.UserId(), cancellationToken);
            return Results.NoContent();
        });
    }

    private static void MapOrders(RouteGroupBuilder orders)
    {
        orders.MapGet("/", (IOrdersService service, HttpContext context, CancellationToken cancellationToken) =>
            service.GetAsync(context.User.UserId(), cancellationToken));
        orders.MapGet("/{orderId}", async (
            string orderId,
            IOrdersService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var order = await service.GetAsync(context.User.UserId(), orderId, cancellationToken);
            return order is null ? Results.NotFound() : Results.Ok(order);
        });
        orders.MapPost("/", async (
            CreateOrderRequest request,
            IOrdersService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var key = context.Request.Headers["Idempotency-Key"].ToString();
            var order = await service.CreateAsync(context.User.UserId(), key, request, cancellationToken);
            return Results.Created($"/api/orders/{order.Id}", order);
        });
    }

    private static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("/product-groups", (ICatalogueService service, CancellationToken cancellationToken) =>
            service.GetGroupsAsync(true, cancellationToken));
        // Soft-deleted rows are never removed, so this list is strictly larger than the public one
        // and only ever grows — it needs paging more than the storefront does, not less.
        admin.MapGet("/products", (
            string? search,
            string? sort,
            string? cursor,
            int? limit,
            ICatalogueService service,
            CancellationToken cancellationToken) =>
            service.GetProductsAsync(
                new ProductQuery(search, sort, IncludeDeleted: true, Cursor: cursor, Limit: limit),
                cancellationToken));
        admin.MapPut("/product-groups/{id}", (string id, ProductGroupWriteRequest request, IAdminCatalogueService service, CancellationToken cancellationToken) =>
            service.UpsertGroupAsync(id, request, cancellationToken));
        admin.MapDelete("/product-groups/{id}", async (string id, IAdminCatalogueService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteGroupAsync(id, cancellationToken);
            return Results.NoContent();
        });
        admin.MapPut("/products/{id}", (string id, ProductWriteRequest request, IAdminCatalogueService service, CancellationToken cancellationToken) =>
            service.UpsertProductAsync(id, request, cancellationToken));
        admin.MapDelete("/products/{id}", async (string id, IAdminCatalogueService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteProductAsync(id, cancellationToken);
            return Results.NoContent();
        });
    }

    private static IResult ToAuthResponse(HttpContext context, AuthResult result)
    {
        context.Response.Cookies.Append(
            RefreshCookie,
            result.RefreshToken,
            RefreshCookieOptions(DateTimeOffset.UtcNow.AddDays(7)));
        return Results.Ok(new
        {
            result.AccessToken,
            result.AccessTokenExpiresAt,
            result.User,
        });
    }

    private static CookieOptions RefreshCookieOptions(DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expires,
    };

    private static Guid UserId(this System.Security.Claims.ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(value, out var id) ? id : throw new AppUnauthorizedException("User identifier is missing.");
    }
}