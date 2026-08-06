namespace ShoppyShop.Infrastructure;

public sealed class FeatureConfigOptions
{
    public const string SectionName = "Features";

    public bool AssistantEnabled { get; set; } = true;
}