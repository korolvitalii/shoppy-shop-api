using System.Reflection;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public static class DatabaseInitializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task InitializeDatabaseAsync(
        this IServiceProvider serviceProvider,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(2026071801)", cancellationToken);
            await SeedCatalogueAsync(dbContext, cancellationToken);
            await SeedIdentityAsync(scope.ServiceProvider, configuration);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(2026071801)", cancellationToken);
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task SeedCatalogueAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        if (await dbContext.ProductGroups.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().Single(x => x.EndsWith("catalogue.json", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded catalogue data could not be loaded.");
        var seed = await JsonSerializer.DeserializeAsync<CatalogueSeed>(
            stream,
            SerializerOptions,
            cancellationToken) ?? throw new InvalidOperationException("Catalogue data is invalid.");

        var groups = seed.Groups.Select((group, index) => new ProductGroup
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            ImageUrl = group.ImageUrl,
            Badge = group.Badge,
            DisplayOrder = index,
        }).ToArray();
        var products = seed.Products.Select(product => new Product
        {
            Id = product.Id,
            GroupId = product.GroupId,
            Name = product.Name,
            Brand = product.Brand,
            Description = product.Description,
            ImageUrl = product.ImageUrl,
            Price = product.Price,
            SalePrice = product.SalePrice,
            InStock = product.InStock,
        }).ToArray();

        dbContext.ProductGroups.AddRange(groups);
        dbContext.Products.AddRange(products);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedIdentityAsync(IServiceProvider services, IConfiguration configuration)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in new[] { "Customer", "Admin" })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole<Guid>(role));
                EnsureSucceeded(result, $"create role {role}");
            }
        }

        var email = configuration["BootstrapAdmin:Email"];
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var userManager = services.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                DisplayName = "Administrator",
                EmailConfirmed = true,
            };
            EnsureSucceeded(await userManager.CreateAsync(user, password), "create bootstrap administrator");
        }

        if (!await userManager.IsInRoleAsync(user, "Admin"))
        {
            EnsureSucceeded(await userManager.AddToRoleAsync(user, "Admin"), "assign administrator role");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Failed to {operation}: {string.Join(", ", result.Errors.Select(x => x.Description))}");
        }
    }

    private sealed record CatalogueSeed(IReadOnlyCollection<GroupSeed> Groups, IReadOnlyCollection<ProductSeed> Products);
    private sealed record GroupSeed(string Id, string Name, string Description, string ImageUrl, string? Badge);
    private sealed record ProductSeed(
        string Id,
        string GroupId,
        string Name,
        string Brand,
        string Description,
        string ImageUrl,
        decimal Price,
        decimal? SalePrice,
        bool InStock);
}