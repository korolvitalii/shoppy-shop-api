using System.Reflection;
using System.Text.Json;

using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public static class CatalogueTranslationSeed
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    public static IReadOnlyList<CatalogueTranslationData> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Data.Translations.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return JsonSerializer.Deserialize<CatalogueTranslationData>(stream, SerializerOptions)
                    ?? throw new InvalidOperationException($"Catalogue translation resource {name} is invalid.");
            }).ToArray();
    }

    internal static void AddTo(AppDbContext dbContext)
    {
        foreach (var data in Load())
        {
            dbContext.ProductTranslations.AddRange(data.Products.Select(p => new ProductTranslation
            {
                ProductId = p.Id,
                Locale = data.Locale,
                Version = data.Version,
                SourceName = p.SourceName,
                SourceDescription = p.SourceDescription,
                Name = p.Name,
                Description = p.Description,
            }));
            dbContext.ProductGroupTranslations.AddRange(data.Groups.Select(g => new ProductGroupTranslation
            {
                GroupId = g.Id,
                Locale = data.Locale,
                Version = data.Version,
                SourceName = g.SourceName,
                SourceDescription = g.SourceDescription,
                SourceBadge = g.SourceBadge,
                Name = g.Name,
                Description = g.Description,
                Badge = g.Badge,
            }));
        }
    }
}

public sealed record CatalogueTranslationData(int Version, string Locale,
    IReadOnlyList<ProductText> Products, IReadOnlyList<GroupText> Groups);
public sealed record ProductText(string Id, string SourceName, string SourceDescription, string Name, string Description);
public sealed record GroupText(string Id, string SourceName, string SourceDescription, string? SourceBadge,
    string Name, string Description, string? Badge);