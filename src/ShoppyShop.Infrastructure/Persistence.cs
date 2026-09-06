using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<ProductGroup> ProductGroups => Set<ProductGroup>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderRequest> OrderRequests => Set<OrderRequest>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ProductGroup>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(80);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.ImageUrl).HasMaxLength(2_000);
            entity.Property(x => x.Badge).HasMaxLength(80);
            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        builder.Entity<Product>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(100);
            entity.Property(x => x.GroupId).HasMaxLength(80);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Brand).HasMaxLength(160);
            entity.Property(x => x.ImageUrl).HasMaxLength(2_000);
            entity.Property(x => x.Price).HasPrecision(12, 2);
            entity.Property(x => x.SalePrice).HasPrecision(12, 2);
            entity.HasOne(x => x.Group).WithMany(x => x.Products).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.GroupId);
            entity.HasIndex(x => new { x.GroupId, x.IsDeleted });

            // Keyset pagination seeks on (sort key, Id), so each sort the catalogue offers needs an
            // index in exactly that shape — otherwise the seek degrades to a scan and sort, and the
            // whole point of paging this way is lost. The price sorts order by
            // COALESCE("SalePrice", "Price"), which is an expression rather than a column, so that
            // index is created as raw SQL in the migration instead of here.
            entity.HasIndex(x => new { x.GroupId, x.Id });
            entity.HasIndex(x => new { x.Name, x.Id });

            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        builder.Entity<Favorite>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.ProductId });
            entity.Property(x => x.ProductId).HasMaxLength(100);
            entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrderNumber).UseIdentityAlwaysColumn();
            entity.HasIndex(x => x.OrderNumber).IsUnique();
            entity.Property(x => x.Subtotal).HasPrecision(12, 2);
            entity.Property(x => x.DeliveryCharge).HasPrecision(12, 2);
            entity.Property(x => x.Total).HasPrecision(12, 2);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.DeliveryName).HasMaxLength(200);
            entity.Property(x => x.DeliveryEmail).HasMaxLength(320);
            entity.Property(x => x.DeliveryAddressLine1).HasMaxLength(300);
            entity.Property(x => x.DeliveryCity).HasMaxLength(120);
            entity.Property(x => x.DeliveryPostcode).HasMaxLength(30);
            entity.Property(x => x.DeliveryCountry).HasMaxLength(100);
            entity.Property(x => x.DeliveryMethod).HasMaxLength(40);
            entity.Property(x => x.PaymentTokenId).HasMaxLength(200);
            entity.Property(x => x.PaymentBrand).HasMaxLength(40);
            entity.Property(x => x.PaymentLast4).HasMaxLength(4);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        builder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductId).HasMaxLength(100);
            entity.Property(x => x.GroupId).HasMaxLength(80);
            entity.Property(x => x.ProductName).HasMaxLength(200);
            entity.Property(x => x.ImageUrl).HasMaxLength(2_000);
            entity.Property(x => x.UnitPrice).HasPrecision(12, 2);
            entity.HasOne(x => x.Order).WithMany(x => x.Lines).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OrderRequest>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.Key });
            entity.Property(x => x.Key).HasMaxLength(200);
            entity.Property(x => x.RequestHash).HasMaxLength(64);
            entity.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.ExpiresAt);
        });

        builder.Entity<RefreshSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.ExpiresAt });
        });
    }
}

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=shoppyshop;Username=shoppyshop;Password=local-development-only";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        return new AppDbContext(options);
    }
}