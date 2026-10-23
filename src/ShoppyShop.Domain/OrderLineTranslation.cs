namespace ShoppyShop.Domain;

/// <summary>A purchase-time display-name snapshot, independent of later catalogue edits.</summary>
public sealed class OrderLineTranslation
{
    public Guid OrderLineId { get; set; }
    public required string Locale { get; set; }
    public required string Name { get; set; }
    public OrderLine? OrderLine { get; set; }
}