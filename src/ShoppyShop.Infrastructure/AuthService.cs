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

        // The unknown-address and wrong-password paths return the same message and do the same
        // password work, so neither the body nor the response time separates them. The locked-out
        // path below does return early and is measurably faster; that matches what SignInManager
        // itself does, and is an accepted trade rather than an oversight.
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Without this the message is a fig leaf: an unregistered address would be rejected in
            // microseconds while a registered one pays full PBKDF2, and the difference is trivially
            // measurable. Hash a throwaway user so both paths cost the same.
            userManager.PasswordHasher.HashPassword(new AppUser(), request.Password);
            throw new AppUnauthorizedException("Invalid email or password.");
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            throw new AppUnauthorizedException("Invalid email or password.");
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await RecordFailedAccessAsync(user);
            throw new AppUnauthorizedException("Invalid email or password.");
        }

        if (await userManager.GetAccessFailedCountAsync(user) > 0)
        {
            await userManager.ResetAccessFailedCountAsync(user);
        }

        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tokenHash = HashToken(refreshToken);

        // A lock-free lookup only to find which user's session family to lock below. Every decision
        // that actually matters (revoked? expired?) is taken from a fresh read after the lock, so
        // this value going stale before the lock is acquired costs nothing.
        var userId = await dbContext.RefreshSessions.AsNoTracking()
            .Where(x => x.TokenHash == tokenHash)
            .Select(x => (Guid?)x.UserId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AppUnauthorizedException("Refresh token is invalid.");

        var strategy = dbContext.Database.CreateExecutionStrategy();
        var (result, reuseDetected) = await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            // Every mutation of a user's refresh-session family — this rotation, reuse revocation
            // below, and the revocation in ChangePasswordAsync — takes this same per-user lock before
            // touching the family, so at most one such operation is ever in flight per user. A single
            // claimed row's lock is not enough: waiting for it only lets a statement re-check a row it
            // already targeted when it started, not discover a row inserted by someone else afterward.
            // Without this, revoking "all of this user's currently live sessions" (below, and in
            // ChangePasswordAsync) can start before a concurrent rotation's replacement session
            // exists in the table, and finish having never seen it — leaving that replacement live
            // despite a reuse that was, in fact, detected.
            await AcquireUserSessionLockAsync(userId, ct);

            var session = await dbContext.RefreshSessions.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, ct)
                ?? throw new AppUnauthorizedException("Refresh token is invalid.");

            if (session.RevokedAt is not null)
            {
                await RevokeFamilyAsync(userId, now, ct);
                await transaction.CommitAsync(ct);
                return (Result: (AuthResult?)null, ReuseDetected: true);
            }

            if (session.ExpiresAt <= now)
            {
                throw new AppUnauthorizedException("Refresh token has expired.");
            }

            // A conditional update, not a tracked mutation, even though the per-user lock above
            // should already make "claimed == 0" impossible here: it costs nothing and means a bug
            // in the lock (wrong key, wrong provider check, a future caller that forgets to take it)
            // fails safe as a detected reuse rather than as a silent lost update that overwrites
            // whatever another, unserialized writer just did to this row.
            var replacementId = Guid.NewGuid();
            var claimed = await dbContext.RefreshSessions
                .Where(x => x.Id == session.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.ReplacedById, replacementId),
                    ct);

            if (claimed == 0)
            {
                await RevokeFamilyAsync(userId, now, ct);
                await transaction.CommitAsync(ct);
                return (Result: (AuthResult?)null, ReuseDetected: true);
            }

            var user = await userManager.FindByIdAsync(userId.ToString())
                ?? throw new AppUnauthorizedException("User no longer exists.");
            var created = await CreateSessionAsync(user, ct, replacementId);
            await transaction.CommitAsync(ct);
            return (Result: created, ReuseDetected: false);
        }, cancellationToken);

        if (reuseDetected)
        {
            throw new AppUnauthorizedException("Refresh token reuse was detected. Please sign in again.");
        }

        return result!;
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
            // Same per-user lock as RefreshAsync: without it, this revoke can start before a
            // concurrent rotation's replacement session exists and never see it.
            await AcquireUserSessionLockAsync(userId, ct);
            var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            ThrowIfIdentityFailed(result);
            await RevokeFamilyAsync(userId, timeProvider.GetUtcNow(), ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Records one failed sign-in, retrying if a concurrent attempt wins the row.
    /// </summary>
    /// <remarks>
    /// <c>AccessFailedAsync</c> increments the count and sets <c>LockoutEnd</c> once it reaches the
    /// configured maximum — without it, <c>MaxFailedAccessAttempts</c> has no effect whatsoever. It
    /// guards the write with the user's <c>ConcurrencyStamp</c> and <em>returns</em> a failed
    /// <c>IdentityResult</c> rather than throwing, so discarding the result loses increments
    /// silently: parallel guesses all read the same count and only one survives. Sequential guessing
    /// would still lock at the limit, but a burst — the shape of attack this exists to stop — would
    /// not. Re-read the user and retry instead.
    /// </remarks>
    private async Task RecordFailedAccessAsync(AppUser user)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if ((await userManager.AccessFailedAsync(user)).Succeeded)
            {
                return;
            }

            // The tracked instance carries the stale stamp that just lost, so it has to be detached
            // before the re-read; otherwise the store hands back the same stale entity.
            dbContext.Entry(user).State = EntityState.Detached;
            if (await userManager.FindByIdAsync(user.Id.ToString()) is not { } reloaded)
            {
                return;
            }

            user = reloaded;
        }
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

    /// <summary>
    /// Serializes every operation that reasons about "all of this user's currently live refresh
    /// sessions" behind one per-user lock, held for the rest of the caller's transaction.
    /// </summary>
    /// <remarks>
    /// SQLite (unit tests) has no advisory-lock equivalent, and those tests drive one request at a
    /// time against a shared connection rather than genuine concurrency, so there is nothing here
    /// for a lock to serialize against.
    /// </remarks>
    private async Task AcquireUserSessionLockAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({ToAdvisoryLockKey(userId)})",
            cancellationToken);
    }

    // pg_advisory_xact_lock takes a single 64-bit key. A Guid has no canonical int64 form, so this
    // only needs to be a stable function of its bytes, not collision-free: a collision would just
    // serialize two unrelated users' operations against each other, never produce a wrong result.
    private static long ToAdvisoryLockKey(Guid userId) => BitConverter.ToInt64(userId.ToByteArray(), 0);

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