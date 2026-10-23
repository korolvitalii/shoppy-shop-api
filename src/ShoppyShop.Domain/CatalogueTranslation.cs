namespace ShoppyShop.Domain;

public sealed class ProductTranslation
{
    public required string ProductId { get; set; }
    public required string Locale { get; set; }
    public required string SourceName { get; set; }
    public required string SourceDescription { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public int Version { get; set; }
    public Product? Product { get; set; }
}

public sealed class ProductGroupTranslation
{
    public required string GroupId { get; set; }
    public required string Locale { get; set; }
    public required string SourceName { get; set; }
    public required string SourceDescription { get; set; }
    public string? SourceBadge { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public string? Badge { get; set; }
    public int Version { get; set; }
    public ProductGroup? Group { get; set; }
}