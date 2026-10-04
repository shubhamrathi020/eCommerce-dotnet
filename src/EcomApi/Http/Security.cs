using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EcomApi.Config;

namespace EcomApi.Http;

/// <summary>What an authenticated request knows about its caller (from the short-lived access token).</summary>
public sealed record AuthUser(string Id, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

/// <summary>
/// HS256 access tokens, hand-rolled so the claims are exactly the Nest API's (sub, roles, permissions, audience and issuer
/// "ecom-api", a jti) and a token from either server is accepted by the other.
/// </summary>
public sealed class JwtService(ApiConfig config)
{
    private const string Audience = "ecom-api";
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}""");

    public string Sign(string userId, IReadOnlyList<string> roles, IReadOnlyList<string> permissions)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["roles"] = roles,
            ["permissions"] = permissions,
            ["iat"] = now,
            ["exp"] = now + config.AccessTokenMinutes * 60L,
            ["aud"] = Audience,
            ["iss"] = Audience,
            ["jti"] = Guid.NewGuid().ToString(),
        });
        var signingInput = $"{Base64Url(Header)}.{Base64Url(payload)}";
        return $"{signingInput}.{Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.JwtAccessSecret), Encoding.ASCII.GetBytes(signingInput)))}";
    }

    /// <summary>The caller a valid token names, or null for anything else (bad signature, wrong algorithm, expired, wrong audience).</summary>
    public AuthUser? Verify(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            using (var header = JsonDocument.Parse(FromBase64Url(parts[0])))
                if (header.RootElement.GetProperty("alg").GetString() != "HS256") return null;
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.JwtAccessSecret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2]))) return null;
            using var doc = JsonDocument.Parse(FromBase64Url(parts[1]));
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!root.TryGetProperty("exp", out var exp) || now >= exp.GetInt64()) return null;
            if (root.TryGetProperty("nbf", out var nbf) && now < nbf.GetInt64()) return null;
            if (!HasValue(root, "aud") || !HasValue(root, "iss")) return null;
            var sub = root.GetProperty("sub").GetString();
            if (string.IsNullOrEmpty(sub)) return null;
            return new AuthUser(sub, Strings(root, "roles"), Strings(root, "permissions"));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static bool HasValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return false;
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Any(v => v.GetString() == Audience) : value.GetString() == Audience;
    }

    private static List<string> Strings(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>The two cookies the API sets, written in the exact format Express uses.</summary>
public static class Cookies
{
    public const string Refresh = "rt";
    public const string Guest = "gcid";
    /// <summary>Scoped to the auth routes, so the refresh token is never sent with ordinary API calls.</summary>
    private const string RefreshPath = "/auth";
    private const long OneYearSeconds = 365L * 86_400;

    public static string? Read(HttpRequest req, string name) => req.Cookies.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static string Http(DateTimeOffset t) => t.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);

    public static void SetRefresh(HttpResponse res, string token, DateTime expiresAtUtc, bool secure) =>
        res.Headers.Append("Set-Cookie", $"{Refresh}={Uri.EscapeDataString(token)}; Path={RefreshPath}; Expires={Http(new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)))}; HttpOnly;{(secure ? " Secure;" : "")} SameSite=Lax");

    public static void ClearRefresh(HttpResponse res, bool secure) =>
        res.Headers.Append("Set-Cookie", $"{Refresh}=; Path={RefreshPath}; Expires=Thu, 01 Jan 1970 00:00:00 GMT; HttpOnly;{(secure ? " Secure;" : "")} SameSite=Lax");

    /// <summary>Returns the guest cart token, creating and setting a fresh cookie the first time one is needed.</summary>
    public static string EnsureGuestToken(HttpRequest req, HttpResponse res, bool secure)
    {
        var existing = Read(req, Guest);
        if (existing is not null) return existing;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        res.Headers.Append("Set-Cookie", $"{Guest}={token}; Max-Age={OneYearSeconds}; Path=/; Expires={Http(DateTimeOffset.UtcNow.AddSeconds(OneYearSeconds))}; HttpOnly;{(secure ? " Secure;" : "")} SameSite=Lax");
        return token;
    }

    /// <summary>The Cart and Order row key for whoever is making the request: the signed-in user, otherwise the guest cart cookie.</summary>
    public static string OwnerKeyFor(string? userId, string guestToken) => userId is not null ? $"user:{userId}" : $"guest:{guestToken}";
}
