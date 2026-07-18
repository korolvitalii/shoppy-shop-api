namespace ShoppyShop.Domain;

public sealed class ProductGroup
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string ImageUrl { get; set; }
    public string? Badge { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
    public List<Product> Products { get; } = [];
}

public sealed class Product
{
    public required string Id { get; set; }
    public required string GroupId { get; set; }
    public required string Name { get; set; }
    public required string Brand { get; set; }
    public required string Description { get; set; }
    public required string ImageUrl { get; set; }
    public decimal Price { get; set; }
    public decimal? SalePrice { get; set; }
    public bool InStock { get; set; }
    public bool IsDeleted { get; set; }
    public ProductGroup? Group { get; set; }
}