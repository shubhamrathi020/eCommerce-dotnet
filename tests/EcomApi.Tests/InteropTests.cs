using EcomApi.Config;
using EcomApi.Http;
using EcomApi.Services;

namespace EcomApi.Tests;

/// <summary>Passwords and tokens made by the other implementations must work here, and the other way round.</summary>
public class InteropTests
{
    private static readonly Dictionary<string, string?> Env = new()
    {
        ["DATABASE_URL"] = "postgresql://x", ["MONGODB_URL"] = "mongodb://x", ["MEILI_URL"] = "http://x", ["REDIS_URL"] = "redis://x", ["RABBITMQ_URL"] = "amqp://x",
        ["JWT_ACCESS_SECRET"] = "test-access-secret-that-is-at-least-32-chars", ["JWT_REFRESH_SECRET"] = "test-refresh-secret-that-is-at-least-32-char",
    };

    /// <summary>Made by the Node server's argon2 package (it writes the parameters as m,p,t).</summary>
    private const string NodeHash = "$argon2id$v=19$m=19456,p=1,t=2$o9vGVs4Mic3MjK9CM51eSA$NZaXA4+vtHEPqn7CVdO5tzmi8HnRyJkoQ8jD8F8KPK8";

    /// <summary>Signed by the Node server's jsonwebtoken package, valid for a century.</summary>
    private const string NodeToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1MSIsInJvbGVzIjpbImFkbWluIl0sInBlcm1pc3Npb25zIjpbInN5c3RlbTpyZWFkIl0sImlhdCI6MTc5MTE1Njk1NCwiZXhwIjo0OTQ0NzU2OTU0LCJhdWQiOiJlY29tLWFwaSIsImlzcyI6ImVjb20tYXBpIiwianRpIjoiYWJjIn0.MoHJgFwSDRU-TR_bU3buScXpqbWICmxAdxUYvuyer54";

    private const string WrongAudienceToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1MSIsInJvbGVzIjpbImFkbWluIl0sInBlcm1pc3Npb25zIjpbInN5c3RlbTpyZWFkIl0sImlhdCI6MTc5MTE1Njk1NCwiZXhwIjo0OTQ0NzU2OTU0LCJhdWQiOiJzb21lb25lLWVsc2UiLCJpc3MiOiJlY29tLWFwaSJ9.Y4Z5QseE_5dq9ULW0LC9WFFO9qh7dABN8lrIdJxCVqk";

    [Fact]
    public async Task VerifiesAHashMadeByTheNodeServer()
    {
        var passwords = new PasswordService();
        Assert.True(await passwords.VerifyAsync(NodeHash, "Str0ngPass"));
        Assert.False(await passwords.VerifyAsync(NodeHash, "str0ngpass"));
    }

    [Fact]
    public async Task HashesAreStandardPhcStringsThatVerifyAgain()
    {
        var passwords = new PasswordService();
        var hash = await passwords.HashAsync("Str0ngPass");
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash);
        Assert.True(await passwords.VerifyAsync(hash, "Str0ngPass"));
        Assert.False(await passwords.VerifyAsync(hash, "Wrong1234"));
        Assert.False(await passwords.VerifyAsync("not a hash", "Str0ngPass"));
        Assert.False(await passwords.VerifyAsync(null, "Str0ngPass")); // unknown account: still does the work, never accepts
    }

    [Fact]
    public void AcceptsATokenSignedByTheNodeServerAndRefusesTamperedOnes()
    {
        var jwt = new JwtService(ApiConfig.Load(Env));
        var user = jwt.Verify(NodeToken);
        Assert.NotNull(user);
        Assert.Equal("u1", user.Id);
        Assert.Equal(["admin"], user.Roles);
        Assert.Contains("system:read", user.Permissions);
        Assert.Null(jwt.Verify(NodeToken[..^2] + "AA"));
        Assert.Null(jwt.Verify(WrongAudienceToken));
        Assert.Null(jwt.Verify("nonsense"));
        var other = new JwtService(ApiConfig.Load(new Over(Env) { ["JWT_ACCESS_SECRET"] = "another-access-secret-that-is-at-least-32" }));
        Assert.Null(other.Verify(NodeToken));
    }

    [Fact]
    public void TokensItSignsRoundTripWithTheClaimsOfTheNestApi()
    {
        var jwt = new JwtService(ApiConfig.Load(Env));
        var token = jwt.Sign("user-1", ["customer"], ["profile:write:own"]);
        var user = jwt.Verify(token);
        Assert.Equal("user-1", user?.Id);
        var payload = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(token.Split('.')[1].Replace('-', '+').Replace('_', '/').PadRight(((token.Split('.')[1].Length + 3) / 4) * 4, '=')));
        Assert.Equal("ecom-api", payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal("ecom-api", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(900, payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64());
    }
}

public class ConfigTests
{
    private static readonly Dictionary<string, string?> Base = new()
    {
        ["DATABASE_URL"] = "postgresql://x", ["MONGODB_URL"] = "mongodb://x", ["MEILI_URL"] = "http://x", ["REDIS_URL"] = "redis://x", ["RABBITMQ_URL"] = "amqp://x",
        ["JWT_ACCESS_SECRET"] = new string('a', 32), ["JWT_REFRESH_SECRET"] = new string('b', 32),
    };

    [Fact]
    public void RefusesToStartWithoutTheRequiredSettingsListingEveryProblem()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ApiConfig.Load(new Dictionary<string, string?>()));
        foreach (var key in new[] { "DATABASE_URL", "MONGODB_URL", "MEILI_URL", "REDIS_URL", "RABBITMQ_URL", "JWT_ACCESS_SECRET", "JWT_REFRESH_SECRET" }) Assert.Contains($"{key} is required", error.Message);
    }

    [Fact]
    public void ValidatesRateLimitOverridesAndSecrets()
    {
        Assert.Contains("RATE_LIMIT_SEARCH_PER_MIN must be a positive number", Assert.Throws<InvalidOperationException>(() => ApiConfig.Load(new Over(Base) { ["RATE_LIMIT_SEARCH_PER_MIN"] = "lots" })).Message);
        Assert.Contains("RATE_LIMIT_COUPON_PER_MIN must be a positive number", Assert.Throws<InvalidOperationException>(() => ApiConfig.Load(new Over(Base) { ["RATE_LIMIT_COUPON_PER_MIN"] = "0" })).Message);
        var config = ApiConfig.Load(new Over(Base) { ["RATE_LIMIT_CHECKOUT_PER_MIN"] = "5" });
        Assert.Equal(new RateLimit(5, 60), config.RateLimits.Checkout);
        Assert.Equal(new RateLimit(60, 60), config.RateLimits.Search);
        Assert.Equal(new RateLimit(300, 60), config.RateLimits.Global);
        Assert.Contains("at least 32 characters", Assert.Throws<InvalidOperationException>(() => ApiConfig.Load(new Over(Base) { ["JWT_ACCESS_SECRET"] = "short" })).Message);
        Assert.Contains("must differ", Assert.Throws<InvalidOperationException>(() => ApiConfig.Load(new Over(Base) { ["JWT_REFRESH_SECRET"] = Base["JWT_ACCESS_SECRET"] })).Message);
        Assert.Contains("test-mode key", Assert.Throws<InvalidOperationException>(() => ApiConfig.Load(new Over(Base) { ["RAZORPAY_KEY_ID"] = "rzp_live_x", ["RAZORPAY_KEY_SECRET"] = "x" })).Message);
    }

    [Fact]
    public void NamespacesStoresForTestsAndParsesOrigins()
    {
        var test = ApiConfig.Load(new Over(Base) { ["NODE_ENV"] = "test" });
        Assert.Equal("ecom.test.", test.MqPrefix);
        Assert.Equal("ecom:test:", test.RedisKeyPrefix);
        Assert.Equal("ecommerce_catalog_test", test.MongoDbName);
        Assert.False(test.RateLimit);
        Assert.Equal("ecom.", ApiConfig.Load(Base).MqPrefix);
        Assert.Equal(["http://a", "http://b"], ApiConfig.Load(new Over(Base) { ["CORS_ORIGINS"] = " http://a , http://b ," }).CorsOrigins);
    }
}

/// <summary>A copy of an environment dictionary with a few settings changed.</summary>
internal sealed class Over(Dictionary<string, string?> baseline) : Dictionary<string, string?>(baseline);
