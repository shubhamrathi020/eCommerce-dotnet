using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EcomApi.Config;
using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace EcomApi.Services;

/// <summary>
/// Argon2id with the OWASP-recommended settings (19 MiB memory, 2 iterations, 1 lane), stored as a standard PHC string so a hash made by
/// any of the API implementations verifies on the others, whatever order the parameters were written in.
/// </summary>
public sealed class PasswordService
{
    private const int Memory = 19_456;
    private const int Iterations = 2;
    private const int Lanes = 1;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    /// <summary>Precomputed so a login for an unknown email costs the same as a real one (no timing leak).</summary>
    private readonly Lazy<Task<string>> dummyHash;

    public PasswordService() => dummyHash = new Lazy<Task<string>>(() => HashAsync("dummy-password-for-timing"));

    public Task<string> HashAsync(string password) => Task.Run(() =>
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Compute(password, salt, Memory, Iterations, Lanes, HashLength);
        return $"$argon2id$v=19$m={Memory},t={Iterations},p={Lanes}${B64(salt)}${B64(hash)}";
    });

    public async Task<bool> VerifyAsync(string? hash, string password)
    {
        var encoded = hash ?? await dummyHash.Value;
        try
        {
            return await Task.Run(() => VerifyPhc(encoded, password));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool VerifyPhc(string encoded, string password)
    {
        var parts = encoded.Split('$');
        // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        if (parts.Length != 6 || parts[1] != "argon2id") return false;
        int m = 0, t = 0, p = 0;
        foreach (var pair in parts[3].Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2) return false;
            switch (kv[0])
            {
                case "m": m = int.Parse(kv[1]); break;
                case "t": t = int.Parse(kv[1]); break;
                case "p": p = int.Parse(kv[1]); break;
            }
        }
        var salt = FromB64(parts[4]);
        var expected = FromB64(parts[5]);
        var actual = Compute(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memory, int iterations, int lanes, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, MemorySize = memory, Iterations = iterations, DegreeOfParallelism = lanes };
        return argon.GetBytes(length);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}

public sealed class OutboxMail
{
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public string SentAt { get; set; } = "";
}

/// <summary>
/// Stand-in for real email delivery. Mails are kept in memory and, outside production, readable at GET /dev/outbox. Links are never
/// logged in production (they grant access).
/// </summary>
public sealed class MailService(ApiConfig config)
{
    private const int Max = 50;
    private readonly object gate = new();
    private readonly List<OutboxMail> outbox = [];

    public void Send(string to, string subject, string body, string? link = null)
    {
        lock (gate)
        {
            outbox.Insert(0, new OutboxMail { To = to, Subject = subject, Body = body, Link = link, SentAt = Derive.Iso(DateTimeOffset.UtcNow) });
            if (outbox.Count > Max) outbox.RemoveRange(Max, outbox.Count - Max);
        }
        Log.Info("Mail", System.Text.Json.JsonSerializer.Serialize(config.Production ? new { @event = "mail.queued", subject, to = (string?)null, link = (string?)null } : new { @event = "mail.queued", subject, to = (string?)to, link }, Json.Web));
    }

    public List<OutboxMail> List()
    {
        lock (gate) return [.. outbox];
    }
}

public sealed record IssuedRefresh(string Token, DateTime ExpiresAt);

/// <summary>
/// Access tokens are short-lived JWTs held in memory by the client. Refresh tokens are random, stored only as hashes, rotated on every
/// use, and grouped in a "family" per sign-in. Presenting a refresh token that was already rotated means it was copied: the whole family
/// is revoked (reuse detection).
/// </summary>
public sealed class TokenService(ApiConfig config, EcomDb db, JwtService jwt)
{
    /// <summary>SHA-256 is right for high-entropy random tokens (unlike passwords, they cannot be guessed).</summary>
    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string AccessToken(string userId, IEnumerable<string> roles)
    {
        var list = roles.ToList();
        return jwt.Sign(userId, list, Roles.PermissionsFor(list));
    }

    /// <summary>Starts a new family (a fresh sign-in).</summary>
    public Task<IssuedRefresh> IssueRefreshAsync(string userId) => CreateAsync(userId, Guid.NewGuid().ToString());

    /// <summary>Exchanges a refresh token for a new one. Throws "unauthorized" when missing, expired, revoked or reused.</summary>
    public async Task<(string UserId, IssuedRefresh Refresh)> RotateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");
        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(r => r.TokenHash == Sha256(token))
            ?? throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");
        var now = DateTime.UtcNow;
        // Atomically claim the token: only one request can turn it from live into rotated.
        var claimed = await db.RefreshTokens.Where(r => r.Id == row.Id && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now));
        if (claimed != 1)
        {
            await RevokeFamilyAsync(row.Family);
            throw new AppError(ApiErrorCode.Unauthorized, "For your safety you have been signed out. Please sign in again.");
        }
        if (row.ExpiresAt <= now) throw new AppError(ApiErrorCode.Unauthorized, "Your session has expired. Please sign in again.");
        var refresh = await CreateAsync(row.UserId, row.Family);
        var replacedBy = Sha256(refresh.Token);
        await db.RefreshTokens.Where(r => r.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ReplacedBy, replacedBy));
        return (row.UserId, refresh);
    }

    /// <summary>The family of a presented refresh token, if it is known (used to keep "this device" signed in).</summary>
    public async Task<string?> FamilyOfAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Sha256(token);
        return await db.RefreshTokens.AsNoTracking().Where(r => r.TokenHash == hash).Select(r => r.Family).FirstOrDefaultAsync();
    }

    public async Task RevokeFamilyAsync(string family)
    {
        var now = DateTime.UtcNow;
        await db.RefreshTokens.Where(r => r.Family == family && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now));
    }

    /// <summary>Signs the user out everywhere, optionally keeping one family (the device making the change).</summary>
    public async Task RevokeAllForUserAsync(string userId, string? exceptFamily = null)
    {
        var now = DateTime.UtcNow;
        await db.RefreshTokens.Where(r => r.UserId == userId && r.RevokedAt == null && (exceptFamily == null || r.Family != exceptFamily)).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now));
    }

    private async Task<IssuedRefresh> CreateAsync(string userId, string family)
    {
        var token = RandomToken();
        var expiresAt = DateTime.UtcNow.AddDays(config.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshTokenRow { Id = Ids.NewId(), UserId = userId, Family = family, TokenHash = Sha256(token), ExpiresAt = expiresAt, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return new IssuedRefresh(token, expiresAt);
    }
}

public static class UserMapper
{
    public static IEnumerable<string> RoleNames(UserRow row) => row.Roles.Select(r => r.ToString());

    /// <summary>Database row to the public User contract. Password hashes and token hashes never leave this function.</summary>
    public static User ToUser(UserRow row)
    {
        var roles = RoleNames(row).ToList();
        return new User
        {
            Id = row.Id,
            Name = row.Name,
            Email = row.Email,
            Phone = string.IsNullOrEmpty(row.Phone) ? null : row.Phone,
            Roles = roles,
            Permissions = Domain.Roles.PermissionsFor(roles),
            EmailVerified = row.EmailVerified,
            CreatedAt = Ids.Iso(row.CreatedAt),
        };
    }

    public static Session ToSession(UserRow row, DateTime expiresAt) => new() { User = ToUser(row), ExpiresAt = Ids.Iso(expiresAt) };
}

/// <summary>What a successful sign-in hands to the controller: the public session plus the two tokens.</summary>
public sealed record SignedIn(Session Session, string AccessToken, IssuedRefresh Refresh);

public sealed class AuthService(EcomDb db, PasswordService passwords, TokenService tokens, MailService mail)
{
    public static readonly Regex Email = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);
    /// <summary>Same brute-force policy as the mock adapter.</summary>
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;
    private const int ResetMinutes = 30;

    public async Task<SignedIn> RegisterAsync(string rawName, string rawEmail, string password)
    {
        var name = rawName.Trim();
        var email = rawEmail.Trim().ToLowerInvariant();
        var fields = new Dictionary<string, string>();
        if (name.Length == 0) fields["name"] = "Name is required";
        else if (name.Length > 80) fields["name"] = "Use 80 characters or fewer";
        if (!Email.IsMatch(email) || email.Length > 254) fields["email"] = "Enter a valid email address";
        var problem = Roles.PasswordProblem(password);
        if (problem is not null) fields["password"] = problem;
        AppError.AssertNoFieldErrors(fields);
        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            // Neutral wording, identical to the mock: never confirm that an address is registered.
            throw new AppError(ApiErrorCode.Validation, "We could not create the account with these details. If you already have an account, sign in or reset your password.");
        }
        var verifyToken = TokenService.RandomToken();
        var user = new UserRow
        {
            Id = Ids.NewId(),
            Name = name,
            Email = email,
            Roles = [RoleKind.customer],
            PasswordHash = await passwords.HashAsync(password),
            VerifyTokenHash = TokenService.Sha256(verifyToken),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        mail.Send(user.Email, "Verify your email address", $"Hi {user.Name}, please confirm your email address to finish setting up your account.", $"/account/verify-email?token={verifyToken}");
        return await SignInAsync(user.Id);
    }

    public async Task<SignedIn> LoginAsync(string rawEmail, string password)
    {
        var email = rawEmail.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        var attempts = await db.LoginAttempts.FirstOrDefaultAsync(a => a.Email == email);
        if (attempts?.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
            throw new AppError(ApiErrorCode.Forbidden, $"Too many failed attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
        // Verify even when the account does not exist, so response time does not reveal it.
        var ok = await passwords.VerifyAsync(user?.PasswordHash, password);
        if (user is null || !ok)
        {
            var fresh = attempts is null || (now - attempts.FirstAt).TotalMinutes > LockoutMinutes;
            var count = fresh ? 1 : attempts!.Count + 1;
            var firstAt = fresh ? now : attempts!.FirstAt;
            DateTime? locked = count >= MaxFailedAttempts ? now.AddMinutes(LockoutMinutes) : null;
            if (attempts is null) db.LoginAttempts.Add(new LoginAttemptRow { Email = email, Count = count, FirstAt = firstAt, LockedUntil = locked });
            else
            {
                attempts.Count = count;
                attempts.FirstAt = firstAt;
                attempts.LockedUntil = locked;
            }
            await db.SaveChangesAsync();
            throw new AppError(ApiErrorCode.Unauthorized, "Incorrect email or password.");
        }
        await db.LoginAttempts.Where(a => a.Email == email).ExecuteDeleteAsync();
        return await SignInAsync(user.Id);
    }

    /// <summary>Exchanges the refresh cookie for a new access token, a rotated refresh token and the current session.</summary>
    public async Task<SignedIn> RefreshAsync(string? refreshToken)
    {
        var (userId, refresh) = await tokens.RotateAsync(refreshToken);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId) ?? throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");
        return new SignedIn(UserMapper.ToSession(user, refresh.ExpiresAt), tokens.AccessToken(user.Id, UserMapper.RoleNames(user)), refresh);
    }

    public async Task LogoutAsync(string? refreshToken)
    {
        var family = await tokens.FamilyOfAsync(refreshToken);
        if (family is not null) await tokens.RevokeFamilyAsync(family);
    }

    public async Task<Session> SessionAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId) ?? throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");
        return UserMapper.ToSession(user, DateTime.UtcNow.AddMinutes(15));
    }

    public async Task VerifyEmailAsync(string token)
    {
        var hash = token.Length > 0 ? TokenService.Sha256(token) : null;
        var user = hash is null ? null : await db.Users.FirstOrDefaultAsync(u => u.VerifyTokenHash == hash);
        if (user is null) throw new AppError(ApiErrorCode.Validation, "This verification link is invalid or has already been used.");
        user.EmailVerified = true;
        user.VerifyTokenHash = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Always succeeds, so the response never reveals whether the address has an account.</summary>
    public async Task RequestPasswordResetAsync(string rawEmail)
    {
        var email = rawEmail.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) return;
        var token = TokenService.RandomToken();
        user.ResetTokenHash = TokenService.Sha256(token);
        user.ResetExpires = DateTime.UtcNow.AddMinutes(ResetMinutes);
        await db.SaveChangesAsync();
        mail.Send(user.Email, "Reset your password", $"Use this link within {ResetMinutes} minutes to choose a new password. If you did not ask for it, ignore this email.", $"/account/reset-password?token={token}");
    }

    public async Task ResetPasswordAsync(string token, string newPassword)
    {
        var problem = Roles.PasswordProblem(newPassword);
        if (problem is not null) throw new AppError(ApiErrorCode.Validation, problem, new Dictionary<string, string> { ["password"] = problem });
        var hash = token.Length > 0 ? TokenService.Sha256(token) : null;
        var user = hash is null ? null : await db.Users.FirstOrDefaultAsync(u => u.ResetTokenHash == hash);
        if (user?.ResetExpires is null || user.ResetExpires.Value < DateTime.UtcNow) throw new AppError(ApiErrorCode.Validation, "This reset link is invalid or has expired.");
        user.PasswordHash = await passwords.HashAsync(newPassword);
        user.ResetTokenHash = null;
        user.ResetExpires = null;
        await db.SaveChangesAsync();
        await db.LoginAttempts.Where(a => a.Email == user.Email).ExecuteDeleteAsync();
        // A password reset signs out every device (the old password may have been stolen).
        await tokens.RevokeAllForUserAsync(user.Id);
    }

    private async Task<SignedIn> SignInAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        var refresh = await tokens.IssueRefreshAsync(user.Id);
        return new SignedIn(UserMapper.ToSession(user, refresh.ExpiresAt), tokens.AccessToken(user.Id, UserMapper.RoleNames(user)), refresh);
    }
}
