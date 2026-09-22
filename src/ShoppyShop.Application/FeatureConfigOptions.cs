namespace ShoppyShop.Application;

/// <summary>
/// Feature switches surfaced to the client, bound from the "Features" configuration section. A
/// configuration contract, not an infrastructure concern - see <see cref="JwtOptions"/>.
/// </summary>
public sealed class FeatureConfigOptions
{
    public const string SectionName = "Features";

    public bool AssistantEnabled { get; set; } = true;
}