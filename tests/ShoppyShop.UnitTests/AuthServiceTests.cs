using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

public sealed class AuthServiceTests
{
    [Fact]
    public async Task RegisterAsyncAlwaysAssignsTheCustomerRoleRegardlessOfRequestData()
    {
        using var fixture = new SqliteAppDbContextFixture();
        await SeedCustomerRoleAsync(fixture);

        var result = await CreateService(fixture).RegisterAsync(
            new RegisterRequest("customer@example.test", "Strong!Password123", "Test Customer"),
            CancellationToken.None);

        Assert.Equal(["Customer"], result.User.Roles);
    }

    [Fact]
    public async Task RefreshAsyncRotatesTheTokenAndRevokesTheWholeSessionChainOnReuse()
    {
        using var fixture = new SqliteAppDbContextFixture();
        await SeedCustomerRoleAsync(fixture);

        // Each CreateService(fixture) call below simulates a separate HTTP request against the
        // same database, matching production's one-DbContext-per-request scoping.
        var initial = await CreateService(fixture).RegisterAsync(
            new RegisterRequest("reuse@example.test", "Strong!Password123", null),
            CancellationToken.None);
        var rotated = await CreateService(fixture).RefreshAsync(initial.RefreshToken, CancellationToken.None);

        Assert.NotEqual(initial.RefreshToken, rotated.RefreshToken);

        // Replaying the original (already-rotated) refresh token is reuse: it must revoke the
        // whole chain, including the token that was legitimately issued by the rotation above.
        await Assert.ThrowsAsync<AppUnauthorizedException>(
            () => CreateService(fixture).RefreshAsync(initial.RefreshToken, CancellationToken.None));
        await Assert.ThrowsAsync<AppUnauthorizedException>(
            () => CreateService(fixture).RefreshAsync(rotated.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task ChangePasswordAsyncRevokesAllActiveRefreshSessions()
    {
        using var fixture = new SqliteAppDbContextFixture();
        await SeedCustomerRoleAsync(fixture);

        var session = await CreateService(fixture).RegisterAsync(
            new RegisterRequest("changer@example.test", "Strong!Password123", null),
            CancellationToken.None);
        await CreateService(fixture).ChangePasswordAsync(
            session.User.Id,
            new ChangePasswordRequest("Strong!Password123", "New!StrongPassword456"),
            CancellationToken.None);

        await Assert.ThrowsAsync<AppUnauthorizedException>(
            () => CreateService(fixture).RefreshAsync(session.RefreshToken, CancellationToken.None));
    }

    private static async Task SeedCustomerRoleAsync(SqliteAppDbContextFixture fixture)
    {
        var (_, roles) = TestSupport.CreateIdentity(fixture.DbContext);
        await roles.CreateAsync(new IdentityRole<Guid>("Customer"));
    }

    private static AuthService CreateService(SqliteAppDbContextFixture fixture)
    {
        var dbContext = fixture.CreateScope();
        var (users, _) = TestSupport.CreateIdentity(dbContext);
        return new AuthService(users, dbContext, Options.Create(TestSupport.CreateJwtOptions()), TimeProvider.System);
    }
}