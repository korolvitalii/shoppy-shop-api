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

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
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
}