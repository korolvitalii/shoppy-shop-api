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

        // Creating the account and giving it its role are one decision, so they commit together.
        // Without the transaction a failed role assignment left a committed user row behind: an
        // account the customer cannot use, which also blocks them retrying with the same address
        // because the email is now taken.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            ThrowIfIdentityFailed(await userManager.CreateAsync(user, request.Password));

            // Checked, unlike before. AddToRoleAsync throws outright when the role does not exist,
            // but it *returns* a failed result when the concurrency-stamped update inside it loses -
            // and discarding that silently registered a user with no role and answered 200.
            ThrowIfIdentityFailed(await userManager.AddToRoleAsync(user, "Customer"));

            var session = await CreateSessionAsync(user, ct);
            await transaction.CommitAsync(ct);
            return session;
        }, cancellationToken);
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

        // The password was checked a few statements ago, against the hash as it stood then. A
        // password change may have committed in between, and ChangePasswordAsync revokes only the
        // sessions that exist when its revoke runs - so a session inserted just after it sweeps past
        // survives, and whoever knew the old password keeps a live seven-day refresh token. Taking
        // the same per-user lock that change holds, then re-reading, closes that window: Identity
        // rolls the security stamp on every password change, so a stamp that still matches means the
        // credential this request verified is still the current one.
        var verifiedStamp = user.SecurityStamp;
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await AcquireUserSessionLockAsync(user.Id, ct);

            var current = await userManager.FindByIdAsync(user.Id.ToString());
            if (current is null || !string.Equals(current.SecurityStamp, verifiedStamp, StringComparison.Ordinal))
            {
                throw new AppUnauthorizedException("Invalid email or password.");
            }

            var session = await CreateSessionAsync(current, ct);
            await transaction.CommitAsync(ct);
            return session;
        }, cancellationToken);
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

        // Chosen once, outside the retried operation, so that every attempt rotates to the same
        // successor. The execution strategy re-runs the whole operation after a transient failure,
        // including one that arrives after the commit has already landed - the connection drops
        // before the acknowledgement does, and the client cannot tell. A retry that drew a fresh
        // successor would find the token already rotated by the attempt before it, call that reuse,
        // and revoke every session the user has: this request's own successor among them.
        var successorId = Guid.NewGuid();
        var successorToken = NewRefreshToken();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        var (result, reuseDetected) = await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            // Every mutation of a user's refresh-session family — this rotation, reuse revocation
            // below, and the revocations in LogoutAsync and ChangePasswordAsync — takes this same
            // per-user lock before touching the family, so at most one such operation is ever in
            // flight per user. A single claimed row's lock is not enough: waiting for it only lets a
            // statement re-check a row it already targeted when it started, not discover a row
            // inserted by someone else afterward. Without this, revoking "all of this user's
            // currently live sessions" (below, in LogoutAsync, and in ChangePasswordAsync) can
            // start before a concurrent rotation's replacement session
            // exists in the table, and finish having never seen it — leaving that replacement live
            // despite a reuse that was, in fact, detected.
            await AcquireUserSessionLockAsync(userId, ct);

            var session = await dbContext.RefreshSessions.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, ct)
                ?? throw new AppUnauthorizedException("Refresh token is invalid.");

            // Expiry is checked before revocation so an expired token is answered the same way
            // whether or not ExpiredRecordCleanupService has deleted its row yet. Checked the other
            // way round, an old rotated token presented once its row had expired would sign the
            // user out everywhere, until cleanup deleted the row and the same request became a plain
            // "invalid".
            if (session.ExpiresAt <= now)
            {
                throw new AppUnauthorizedException("Refresh token has expired.");
            }

            if (session.RevokedAt is not null)
            {
                // Rotated by an earlier attempt of this same request, whose commit landed although
                // the attempt itself failed. Answer as that attempt would have, with the successor
                // it created - provided nothing has ended that successor since.
                if (session.ReplacedById == successorId)
                {
                    return (Result: await ResumeRotationAsync(userId, successorId, successorToken, now, ct), ReuseDetected: false);
                }

                // Only a token that was rotated has been handed to someone else as well: its
                // successor went to whoever rotated it, so seeing it again means two holders. A token
                // revoked by logout or a password change has no successor, and presenting it again is
                // a stale cookie, not theft. Treating that as reuse signed the user out on every other
                // device each time an old tab retried after signing out.
                if (session.ReplacedById is null)
                {
                    throw new AppUnauthorizedException("Refresh token is invalid.");
                }

                await RevokeFamilyAsync(userId, now, ct);
                await transaction.CommitAsync(ct);
                return (Result: (AuthResult?)null, ReuseDetected: true);
            }

            // A conditional update, not a tracked mutation, even though the per-user lock above
            // should already make "claimed == 0" impossible here: it costs nothing and means a bug
            // in the lock (wrong key, wrong provider check, a future caller that forgets to take it)
            // fails safe as a detected reuse rather than as a silent lost update that overwrites
            // whatever another, unserialized writer just did to this row.
            var claimed = await dbContext.RefreshSessions
                .Where(x => x.Id == session.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.ReplacedById, successorId),
                    ct);

            if (claimed == 0)
            {
                await RevokeFamilyAsync(userId, now, ct);
                await transaction.CommitAsync(ct);
                return (Result: (AuthResult?)null, ReuseDetected: true);
            }

            var user = await userManager.FindByIdAsync(userId.ToString())
                ?? throw new AppUnauthorizedException("User no longer exists.");
            var created = await CreateSessionAsync(user, ct, successorId, successorToken);
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

        var tokenHash = HashToken(refreshToken);

        // Lock-free, as in RefreshAsync: only which family to lock. The decision below is taken
        // from a fresh read after the lock.
        var userId = await dbContext.RefreshSessions.AsNoTracking()
            .Where(x => x.TokenHash == tokenHash)
            .Select(x => (Guid?)x.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (userId is null)
        {
            return;
        }

        // Without the lock, a refresh of the same token could rotate it between this method reading
        // the session and writing it. The write then revoked a row that was already revoked, and the
        // replacement the refresh had just issued stayed live for its full seven days: the user had
        // signed out, and the session had not ended.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await AcquireUserSessionLockAsync(userId.Value, ct);

            var now = timeProvider.GetUtcNow();
            var session = await dbContext.RefreshSessions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

            // Expired first, as in RefreshAsync: an expired token can no longer do anything, so it
            // must not sign out newer sessions just because cleanup has not deleted its row yet.
            if (session is null || session.ExpiresAt <= now)
            {
                return;
            }

            if (session.RevokedAt is null)
            {
                await dbContext.RefreshSessions
                    .Where(x => x.Id == session.Id && x.RevokedAt == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
            }
            else if (session.ReplacedById is not null)
            {
                // Already rotated: either a refresh won the race above, or this cookie is a token
                // someone else has already used. Either way its successor is out there, and the
                // family has no marker of which rows descend from this login, so every live session
                // goes, which is the same rule RefreshAsync applies to reuse.
                await RevokeFamilyAsync(userId.Value, now, ct);
            }

            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    public async Task<UserDto?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : await MapUserAsync(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        // Identity passes CurrentPassword straight to the password hasher, which throws
        // ArgumentNullException on null - and an unhandled ArgumentNullException is a 500, so a
        // malformed request would be reported as a server fault. NewPassword needs no guard of its
        // own: Identity's password validator rejects null itself, and that already arrives as a 400.
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            throw new AppValidationException("Current password is required.");
        }

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
        Guid? sessionId = null,
        string? refreshToken = null)
    {
        refreshToken ??= NewRefreshToken();
        var now = timeProvider.GetUtcNow();
        dbContext.RefreshSessions.Add(new RefreshSession
        {
            Id = sessionId ?? Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.RefreshTokenDays),
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return await IssueTokensAsync(user, refreshToken);
    }

    /// <summary>
    /// Completes a rotation that an earlier attempt of the same refresh committed: the session row
    /// already exists, so this only checks that it is still live and mints a new access token for it.
    /// </summary>
    /// <remarks>
    /// The successor is matched on its token hash as well as its id, so this can hand back only the
    /// token whose hash the earlier attempt stored. The per-user lock was released between the
    /// attempts, so a logout or password change may have ended the successor in that gap; it is then
    /// answered like any other ended token rather than revived.
    /// </remarks>
    private async Task<AuthResult> ResumeRotationAsync(
        Guid userId,
        Guid successorId,
        string successorToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var successorHash = HashToken(successorToken);
        var successorLive = await dbContext.RefreshSessions.AnyAsync(
            x => x.Id == successorId && x.TokenHash == successorHash && x.RevokedAt == null && x.ExpiresAt > now,
            cancellationToken);
        if (!successorLive)
        {
            throw new AppUnauthorizedException("Refresh token is invalid.");
        }

        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new AppUnauthorizedException("User no longer exists.");
        return await IssueTokensAsync(user, successorToken);
    }

    /// <summary>
    /// Mints an access token for <paramref name="user"/> and pairs it with
    /// <paramref name="refreshToken"/>, whose session row the caller has already stored.
    /// </summary>
    private async Task<AuthResult> IssueTokensAsync(AppUser user, string refreshToken)
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

    private static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

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