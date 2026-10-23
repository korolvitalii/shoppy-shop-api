using Microsoft.EntityFrameworkCore;

using ShoppyShop.Application;
using ShoppyShop.Domain;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

public sealed class CatalogueLocalizationTests
{
    [Fact]
    public async Task OrdersSnapshotEveryAvailableLocaleAndDoNotRenameHistoricalPurchases()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var db = fixture.DbContext;
        db.ProductGroups.Add(new ProductGroup { Id = "test", Name = "Furniture", Description = "Objects", ImageUrl = "/group" });
        var product = new Product { Id = "a", GroupId = "test", Name = "Chair", Description = "Source", Brand = "Test", ImageUrl = "/product", Price = 10, InStock = true };
        foreach (var (locale, name) in new[] { ("fr", "Chaise"), ("pt-BR", "Cadeira") })
            product.Translations.Add(new ProductTranslation { ProductId = "a", Locale = locale, SourceName = "Chair", SourceDescription = "Source", Name = name, Description = "Translation", Version = 1 });
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var selected = new CatalogueLocale("en", ["en", "fr", "pt-BR"]);
        selected.Select("fr");
        var service = new OrdersService(db, TimeProvider.System, selected);
        var user = Guid.NewGuid();
        var request = new CreateOrderRequest([new OrderItemRequest("a", "test", "Ignored client name", "/product", 1, 1)],
            new DeliveryAddress("Alex", "alex@example.test", "1 Street", "City", "12345", "France"), "standard", new PaymentSummary("test", "Visa", "4242"), 0, 0, 0);
        var order = await service.CreateAsync(user, "translation-snapshot", request, CancellationToken.None);
        Assert.Equal("Chair", Assert.Single(order.Lines).Name);
        Assert.Equal("Chaise", Assert.Single(order.Lines).DisplayName);
        (await db.Products.SingleAsync()).Name = "Renamed catalogue item";
        await db.SaveChangesAsync();
        selected.Select("pt-BR");
        var receipt = await service.GetAsync(user, order.Id, CancellationToken.None);
        Assert.Equal("Cadeira", Assert.Single(receipt!.Lines).DisplayName);
        Assert.Equal("Chair", Assert.Single(receipt.Lines).Name);
    }

    [Fact]
    public async Task AnAdditionalConfiguredLocaleSupportsLookupSearchSortAndLocaleBoundCursors()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var db = fixture.DbContext;
        var group = new ProductGroup { Id = "test", Name = "Furniture", Description = "Objects", ImageUrl = "/group" };
        db.ProductGroups.Add(group);
        foreach (var (id, name, translated) in new[] { ("a", "Chair", "Chaise"), ("b", "Desk", "Bureau"), ("c", "Lamp", "Lampe") })
        {
            var product = new Product { Id = id, GroupId = group.Id, Name = name, Description = "Source", Brand = "Test", ImageUrl = "/product", Price = 10, InStock = true };
            product.Translations.Add(new ProductTranslation { ProductId = id, Locale = "fr", SourceName = name, SourceDescription = "Source", Name = translated, Description = "Mobilier", Version = 1 });
            db.Products.Add(product);
        }
        await db.SaveChangesAsync();
        var locale = new CatalogueLocale("en", ["en", "fr"]);
        locale.Select("fr");
        var service = new CatalogueService(db, locale);
        Assert.Equal("Chaise", (await service.GetProductAsync("test", "a", false, CancellationToken.None))!.Name);
        var search = await service.GetProductsAsync(new ProductQuery(Search: "Chaise"), CancellationToken.None);
        Assert.Equal("a", Assert.Single(search.Items).Id);
        var first = await service.GetProductsAsync(new ProductQuery(Sort: "name", Limit: 1), CancellationToken.None);
        Assert.Equal("Bureau", Assert.Single(first.Items).Name);
        var second = await service.GetProductsAsync(new ProductQuery(Sort: "name", Limit: 1, Cursor: first.NextCursor), CancellationToken.None);
        Assert.Equal("Chaise", Assert.Single(second.Items).Name);
        locale.Select("en");
        var error = await Assert.ThrowsAsync<AppValidationException>(() => service.GetProductsAsync(new ProductQuery(Sort: "name", Cursor: first.NextCursor), CancellationToken.None));
        Assert.Equal("catalogue.cursor_invalid", error.Code);
        Assert.Throws<AppValidationException>(() => locale.Select("unconfigured"));
    }

    [Fact]
    public async Task SourceEditsInvalidateTranslationsWithoutOverwritingTheSource()
    {
        using var fixture = new SqliteAppDbContextFixture();
        var db = fixture.DbContext;
        var product = new Product { Id = "a", GroupId = "test", Name = "Edited chair", Description = "New description", Brand = "Test", ImageUrl = "/product", Price = 10 };
        db.ProductGroups.Add(new ProductGroup { Id = "test", Name = "Furniture", Description = "Objects", ImageUrl = "/group" });
        product.Translations.Add(new ProductTranslation { ProductId = "a", Locale = "fr", SourceName = "Chair", SourceDescription = "Source", Name = "Chaise", Description = "Mobilier", Version = 1 });
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var locale = new CatalogueLocale("en", ["en", "fr"]);
        locale.Select("fr");
        var actual = await new CatalogueService(db, locale).GetProductAsync("test", "a", false, CancellationToken.None);
        Assert.Equal("Edited chair", actual!.Name);
        Assert.Equal("New description", actual.Description);
        Assert.Equal("Edited chair", (await db.Products.SingleAsync()).Name);
    }
}