namespace ShoppyShop.Application;

/// <summary>
/// Token issuing parameters, bound from the "Jwt" configuration section.
/// </summary>
/// <remarks>
/// This is a configuration contract rather than an infrastructure concern, so it lives here and not
/// beside the service that reads it: the API project needs it at startup to configure JwtBearer, and
/// having it in Infrastructure was what forced ShoppyShop.Api to reference Infrastructure types from
/// its endpoint and composition code rather than only from its composition root.
/// </remarks>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 7;
}