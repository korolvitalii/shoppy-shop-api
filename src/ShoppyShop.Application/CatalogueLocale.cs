namespace ShoppyShop.Application;

/// <summary>The configured source locale is used when an older client omits its locale.</summary>
public sealed class CatalogueLocale(string defaultLocale = CatalogueLocale.LegacyDefault,
    IReadOnlyCollection<string>? supportedLocales = null)
{
    public const string LegacyDefault = "en";
    public string DefaultLocale { get; } = defaultLocale;
    public string Value { get; private set; } = defaultLocale;
    public bool IsSource => Value == DefaultLocale;

    public void Select(string? value)
    {
        var candidate = string.IsNullOrEmpty(value) ? DefaultLocale : value;
        Value = (supportedLocales ?? [DefaultLocale]).FirstOrDefault(locale =>
            string.Equals(locale, candidate, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppValidationException("The requested locale is not supported.", code: "locale.invalid");
    }
}