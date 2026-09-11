using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure;

namespace ShoppyShop.UnitTests;

public sealed class AuthServiceTests
{
    [Fact]
    public async Task LoginAsyncLocksTheAccountAfterTheConfiguredFailureCountAndRejectsTheRealPassword()
    {
        using var fixture = new SqliteAppDbContextFixture();
        await SeedCustomerRoleAsync(fixture);
        await CreateService(fixture).RegisterAsync(
            new RegisterRequest("locked@example.test", "Strong!Password123", null),
            CancellationToken.None);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<AppUnauthorizedException>(() => CreateService(fixture).LoginAsync(
                new LoginRequest("locked@example.test", "Wrong!Password123"),
                CancellationToken.None));
        }

        // The account is now locked, so even the correct password must be refused — and with the
        // same message, so the response does not confirm that the guess was right.
        var error = await Assert.ThrowsAsync<AppUnauthorizedException>(() => CreateService(fixture).LoginAsync(
            new LoginRequest("locked@example.test", "Strong!Password123"),
            CancellationToken.None));
        Assert.Equal("Invalid email or password.", error.Message);
    }

    [Fact]
    public async Task LoginAsyncAppliesTheConfiguredLockoutWindowRatherThanTheIdentityDefault()
    {
        using var fixture = new SqliteAppDbContextFixture();
        await SeedCustomerRoleAsync(fixture);
        const string email = "window@example.test";
        await CreateService(fixture).RegisterAsync(
            new RegisterRequest(email, "Strong!Password123", null),
            CancellationToken.None);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<AppUnauthorizedException>(() => CreateService(fixture).LoginAsync(
                new LoginRequest(email, "Wrong!Password123"),
                CancellationToken.None));
        }

        // Identity's default window is 5 minutes and its default attempt count is 5, so a test that
        // only counts attempts passes whether or not this application's configuration is applied at
        // all. The window is the part that differs, so it is the part worth asserting.
        var user = await fixture.DbContext.Users.AsNoTracking().SingleAsync(x => x.Email == email);
        Assert.NotNull(user.LockoutEnd);
        Assert.InRange((user.LockoutEnd!.Value - DateTimeOffset.UtcNow).TotalMinutes, 14, 15);
    }

    [Fact]
    public async Task LoginAsyncResetsTheFailureCountAfterASuccessfulSignIn()
    {
        using var fixture = new SqliteAppDbContextFixture();
        await SeedCustomerRoleAsync(fixture);
        await CreateService(fixture).RegisterAsync(
            new RegisterRequest("recovers@example.test", "Strong!Password123", null),
            CancellationToken.None);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<AppUnauthorizedException>(() => CreateService(fixture).LoginAsync(
                new LoginRequest("recovers@example.test", "Wrong!Password123"),
                CancellationToken.None));
        }

        await CreateService(fixture).LoginAsync(
            new LoginRequest("recovers@example.test", "Strong!Password123"),
            CancellationToken.None);

        // Without the reset, four earlier failures plus one new one would lock an account that has
        // since proved it knows the password.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<AppUnauthorizedException>(() => CreateService(fixture).LoginAsync(
                new LoginRequest("recovers@example.test", "Wrong!Password123"),
                CancellationToken.None));
        }

        var result = await CreateService(fixture).LoginAsync(
            new LoginRequest("recovers@example.test", "Strong!Password123"),
            CancellationToken.None);
        Assert.NotEmpty(result.AccessToken);
    }

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