using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

/// <summary>
/// A real (SQLite, in-memory) relational database per test. The EF Core InMemory provider does not
/// support <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>, which <see cref="AuthService"/> and
/// <see cref="AdminCatalogueService"/> rely on, so it cannot exercise this code faithfully.
/// </summary>
internal sealed class SqliteAppDbContextFixture : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly List<AppDbContext> scopes = [];

    public AppDbContext DbContext { get; }

    public SqliteAppDbContextFixture()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        DbContext = CreateScope();
        DbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// A new <see cref="AppDbContext"/> against the same underlying database, with an empty
    /// change tracker — mirroring the fresh, request-scoped <c>DbContext</c> that ASP.NET Core
    /// creates per HTTP request in production. Reusing one instance across sequential calls (unlike
    /// production) can surface stale tracked values after an <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>.
    /// </summary>
    public AppDbContext CreateScope()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var scope = new AppDbContext(options);
        scopes.Add(scope);
        return scope;
    }

    public void Dispose()
    {
        foreach (var scope in scopes)
        {
            scope.Dispose();
        }

        connection.Dispose();
    }
}

internal static class TestSupport
{
    public static JwtOptions CreateJwtOptions() => new()
    {
        Issuer = "ShoppyShop.Tests",
        Audience = "ShoppyShop.Tests",
        SigningKey = "unit-test-signing-key-that-is-at-least-thirty-two-bytes-long",
    };

    public static (UserManager<AppUser> Users, RoleManager<IdentityRole<Guid>> Roles) CreateIdentity(AppDbContext dbContext)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AppDbContext>(dbContext);
        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<UserManager<AppUser>>(), provider.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
    }
}