using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure.Assistant;

namespace ShoppyShop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var host = configuration["Database:Host"];
            var database = configuration["Database:Name"];
            var username = configuration["Database:Username"];
            var password = configuration["Database:Password"];
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) ||
                string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("PostgreSQL connection configuration is required.");
            }

            connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = 5432,
                Database = database,
                Username = username,
                Password = password,
                SslMode = SslMode.VerifyFull,
            }.ConnectionString;
        }

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null)));
        services.AddIdentityCore<AppUser>(ConfigureIdentity)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<FeatureConfigOptions>(configuration.GetSection(FeatureConfigOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICatalogueService, CatalogueService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IFavoritesService, FavoritesService>();
        services.AddScoped<IOrdersService, OrdersService>();
        services.AddScoped<IAdminCatalogueService, AdminCatalogueService>();
        services.AddSingleton<IAssistantModelClient, AnthropicAssistantModelClient>();
        services.AddScoped<IAssistantService, AssistantService>();
        return services;
    }

    /// <summary>
    /// The one definition of identity policy. Tests build their <c>UserManager</c> through this too,
    /// so the lockout tests exercise these values instead of silently passing on Identity's defaults
    /// — which happen to agree on the attempt count but not on the window.
    /// </summary>
    /// <remarks>
    /// Lockout is a deliberate trade, not a free win: five wrong passwords will lock a known address
    /// for fifteen minutes, which is inside the per-IP auth budget and gives anyone who knows an
    /// email a cheap way to deny that account. The count resets only on a successful sign-in, so
    /// there is no unlock path but waiting. That is the standard trade for stopping online guessing,
    /// and it is recorded here so the next reader knows it was chosen rather than inherited.
    /// </remarks>
    public static void ConfigureIdentity(IdentityOptions options)
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    }
}