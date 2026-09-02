using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using ShoppyShop.Application;
using ShoppyShop.Domain;

namespace ShoppyShop.Infrastructure;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 7;
}

public sealed class AuthService(
    UserManager<AppUser> userManager,
    AppDbContext dbContext,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IAuthService
{
    private readonly JwtOptions options = jwtOptions.Value;

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new AppValidationException("Email and password are required.");
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Email.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName?.Trim(),
            EmailConfirmed = true,
        };

        var result = await userManager.CreateAsync(user, request.Password);
        ThrowIfIdentityFailed(result);
        await userManager.AddToRoleAsync(user, "Customer");
        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new AppValidationException("Email and password are required.");
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            throw new AppUnauthorizedException("Invalid email or password.");
        }

        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tokenHash = HashToken(refreshToken);
        var session = await dbContext.RefreshSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (session is null)
        {
            throw new AppUnauthorizedException("Refresh token is invalid.");
        }

        if (session.RevokedAt is not null)
        {
            await RevokeFamilyAsync(session.UserId, now, cancellationToken);
            throw new AppUnauthorizedException("Refresh token reuse was detected. Please sign in again.");
        }

        if (session.ExpiresAt <= now)
        {
            throw new AppUnauthorizedException("Refresh token has expired.");
        }

        // Claim the token with a conditional update rather than a tracked mutation: only the
        // request that actually flips RevokedAt from null may rotate it. Without this, two
        // concurrent refreshes of the same token both pass the check above and both mint a
        // session, which is exactly the case reuse detection exists to catch.
        var replacementId = Guid.NewGuid();
        var claimed = await dbContext.RefreshSessions
            .Where(x => x.Id == session.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(
                x => x.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.ReplacedById, replacementId),
                cancellationToken);

        if (claimed == 0)
        {
            await RevokeFamilyAsync(session.UserId, now, cancellationToken);
            throw new AppUnauthorizedException("Refresh token reuse was detected. Please sign in again.");
        }

        var user = await userManager.FindByIdAsync(session.UserId.ToString())
            ?? throw new AppUnauthorizedException("User no longer exists.");
        return await CreateSessionAsync(user, cancellationToken, replacementId);
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var hash = HashToken(refreshToken);
        var session = await dbContext.RefreshSessions.SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<UserDto?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : await MapUserAsync(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        // Both writes belong to one decision: if the revocation fails after the hash is
        // persisted, the password has changed while every existing session stays valid.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            var user = await userManager.FindByIdAsync(userId.ToString())
                ?? throw new AppNotFoundException("User was not found.");

            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            ThrowIfIdentityFailed(result);
            await RevokeFamilyAsync(userId, timeProvider.GetUtcNow(), ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    private async Task<AuthResult> CreateSessionAsync(
        AppUser user,
        CancellationToken cancellationToken,
        Guid? sessionId = null)
    {
        var now = timeProvider.GetUtcNow();
        var expiry = now.AddMinutes(options.AccessTokenMinutes);
        var roles = await userManager.GetRolesAsync(user);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, now.UtcDateTime, expiry.UtcDateTime, credentials);
        var refreshToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

        dbContext.RefreshSessions.Add(new RefreshSession
        {
            Id = sessionId ?? Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.RefreshTokenDays),
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResult(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiry,
            refreshToken,
            await MapUserAsync(user));
    }

    private async Task<UserDto> MapUserAsync(AppUser user) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, [.. await userManager.GetRolesAsync(user)]);

    private Task<int> RevokeFamilyAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.RefreshSessions
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), cancellationToken);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static void ThrowIfIdentityFailed(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = result.Errors
            .GroupBy(x => x.Code)
            .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray());
        throw new AppValidationException("Identity validation failed.", errors);
    }
}