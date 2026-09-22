using System.Reflection;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public static class DatabaseInitializer
{
    private const long StartupLockKey = 2026071801;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, long, Exception> LogStartupLockUnlockFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(1, nameof(LogStartupLockUnlockFailed)),
            "Failed to explicitly release the startup advisory lock ({LockKey}); the connection close below still releases it at the session level.");

    public static async Task InitializeDatabaseAsync(
        this IServiceProvider serviceProvider,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ShoppyShop.Infrastructure.DatabaseInitializer");

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // Taken before the migration, not after it. Rolling deployments and any scale-out past
            // one instance start containers concurrently, and two of them issuing the same CREATE
            // INDEX / ALTER TABLE race on __EFMigrationsHistory and on the DDL itself. Postgres DDL
            // is transactional, so that usually surfaces as one instance crash-looping rather than
            // as corruption - but a failed start mid-deploy is still an outage, and a
            // nondeterministic one. The lock is session-scoped and this connection is held open, so
            // it covers the migration as well as the seed.
            await dbContext.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({StartupLockKey})", cancellationToken);

            if (configuration.GetValue("Database:AutoMigrate", true))
            {
                await dbContext.Database.MigrateAsync(cancellationToken);
            }

            await SeedCatalogueAsync(dbContext, cancellationToken);
            await SeedIdentityAsync(scope.ServiceProvider, configuration);
        }
        finally
        {
            // Explicit unlock, not just a close: Npgsql pools the physical connection by default,
            // and its pool reset (which is what would otherwise clear session state such as an
            // advisory lock) is deferred to that connection's *next* checkout rather than running
            // when we close it here. Until something reuses it from the pool, the pooled physical
            // session still holds pg_advisory_lock, which blocks another instance's startup on the
            // same lock key for no reason - a second rolling-deploy instance can time out waiting
            // on a lock the first instance believes it already released.
            // https://www.npgsql.org/doc/basic-usage.html (connection pooling / RESET on return)
            try
            {
                // CancellationToken.None: if we got here because the token above was cancelled,
                // still attempt the unlock rather than skipping straight to CloseConnectionAsync.
                await dbContext.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({StartupLockKey})", CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Never let a failed unlock replace whatever exception the try block above is
                // already propagating (or turn a clean run into a failure). If the connection is
                // broken - e.g. the seed above failed because the connection died - this throws
                // too, so it's caught and logged rather than let out. CloseConnectionAsync below
                // is the backstop either way: ending the session releases every session-level
                // lock it still holds, including this one if the explicit unlock couldn't run.
                LogStartupLockUnlockFailed(logger, StartupLockKey, ex);
            }

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
            IsNew = product.IsNew,
            GiftWrappable = product.GiftWrappable,
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
            EnsureSucceeded(await userManager.AddToRoleAsync(user, "Admin"), "assign administrator role");
            return;
        }

        if (!await userManager.IsInRoleAsync(user, "Admin"))
        {
            throw new InvalidOperationException(
                $"BootstrapAdmin:Email ('{email}') matches an existing account that is not an administrator. " +
                "Refusing to promote it automatically. Promote it explicitly instead, or change BootstrapAdmin:Email " +
                "to an address with no existing account.");
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
        bool InStock,
        bool IsNew = false,
        bool GiftWrappable = false);
}