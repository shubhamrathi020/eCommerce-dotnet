using System.Globalization;

namespace EcomApi.Config;

public sealed record RateLimit(int Limit, int WindowSeconds);

public sealed record RateLimits(RateLimit Global, RateLimit Search, RateLimit Coupon, RateLimit Checkout)
{
    public RateLimit this[string bucket] => bucket switch
    {
        "global" => Global,
        "search" => Search,
        "coupon" => Coupon,
        "checkout" => Checkout,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };
}

/// <summary>Typed, validated configuration read once from the environment. The app refuses to start without it.</summary>
public sealed class ApiConfig
{
    public int Port { get; init; }
    public bool Production { get; init; }
    public string DatabaseUrl { get; init; } = "";
    /// <summary>Catalog store: flexible product, category, brand and collection documents.</summary>
    public string MongoUrl { get; init; } = "";
    public string MongoDbName { get; init; } = "";
    /// <summary>Search engine: typo-tolerant, faceted product search.</summary>
    public string MeiliUrl { get; init; } = "";
    public string MeiliMasterKey { get; init; } = "";
    /// <summary>Suffix appended to Meilisearch index names, so a test run never touches dev or prod data.</summary>
    public string MeiliIndexSuffix { get; init; } = "";
    public string RedisUrl { get; init; } = "";
    /// <summary>Prefix on every Redis key, so a test run never touches dev or prod cache entries.</summary>
    public string RedisKeyPrefix { get; init; } = "";
    public string RabbitmqUrl { get; init; } = "";
    /// <summary>Every exchange, queue and lock name is prefixed with this, so a test run never shares topology with dev or prod.</summary>
    public string MqPrefix { get; init; } = "";
    /// <summary>Minutes an item can sit untouched in a cart before one reminder email is sent.</summary>
    public int AbandonedCartMinutes { get; init; }
    public RateLimits RateLimits { get; init; } = null!;
    public string JwtAccessSecret { get; init; } = "";
    public string JwtRefreshSecret { get; init; } = "";
    public string RazorpayKeyId { get; init; } = "";
    public string RazorpayKeySecret { get; init; } = "";
    public string RazorpayWebhookSecret { get; init; } = "";
    public bool RazorpayEnabled { get; init; }
    /// <summary>Origins allowed to call the API with credentials.</summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
    /// <summary>Rate limiting is on everywhere except the automated test run (unless a test switches it back on).</summary>
    public bool RateLimit { get; init; }

    public static ApiConfig Load(IDictionary<string, string?> env)
    {
        var problems = new List<string>();
        string Get(string key) => env.TryGetValue(key, out var v) ? v?.Trim() ?? "" : "";
        string Need(string key)
        {
            var value = Get(key);
            if (value.Length == 0) problems.Add($"{key} is required");
            return value;
        }
        int Int(string key, int fallback)
        {
            var raw = Get(key);
            if (raw.Length == 0) return fallback;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n <= 0)
            {
                problems.Add($"{key} must be a positive number");
                return fallback;
            }
            return (int)n;
        }

        var databaseUrl = Need("DATABASE_URL");
        var mongoUrl = Need("MONGODB_URL");
        var meiliUrl = Need("MEILI_URL");
        var meiliMasterKey = Get("MEILI_MASTER_KEY");
        var redisUrl = Need("REDIS_URL");
        var rabbitmqUrl = Need("RABBITMQ_URL");
        var access = Need("JWT_ACCESS_SECRET");
        var refresh = Need("JWT_REFRESH_SECRET");
        foreach (var (key, value) in new[] { ("JWT_ACCESS_SECRET", access), ("JWT_REFRESH_SECRET", refresh) })
            if (value.Length > 0 && value.Length < 32) problems.Add($"{key} must be at least 32 characters");
        if (access.Length > 0 && access == refresh) problems.Add("JWT_ACCESS_SECRET and JWT_REFRESH_SECRET must differ");
        var nodeEnv = Get("NODE_ENV").Length > 0 ? Get("NODE_ENV") : Get("ASPNETCORE_ENVIRONMENT") switch { "Production" => "production", "Development" => "development", var other => other };
        var production = nodeEnv == "production";
        var isTest = nodeEnv == "test";
        if (production && access.StartsWith("dev-only-", StringComparison.Ordinal))
            Console.Error.WriteLine("[config] Using the dev-only JWT secrets from .env.example. Set real secrets before deploying anywhere.");
        var razorpayKeyId = Get("RAZORPAY_KEY_ID");
        var razorpayKeySecret = Get("RAZORPAY_KEY_SECRET");
        if (razorpayKeyId.Length > 0 && !razorpayKeyId.StartsWith("rzp_test_", StringComparison.Ordinal))
            problems.Add("RAZORPAY_KEY_ID must be a test-mode key (starts with 'rzp_test_'); live keys are refused");
        if (razorpayKeyId.Length > 0 && razorpayKeySecret.Length == 0) problems.Add("RAZORPAY_KEY_SECRET is required when RAZORPAY_KEY_ID is set");
        var rateLimits = new RateLimits(
            new RateLimit(Int("RATE_LIMIT_GLOBAL_PER_MIN", 300), 60),
            new RateLimit(Int("RATE_LIMIT_SEARCH_PER_MIN", 60), 60),
            new RateLimit(Int("RATE_LIMIT_COUPON_PER_MIN", 20), 60),
            new RateLimit(Int("RATE_LIMIT_CHECKOUT_PER_MIN", 20), 60));
        var abandoned = Int("ABANDONED_CART_MINUTES", 60);
        if (problems.Count > 0) throw new InvalidOperationException("Invalid API configuration:\n - " + string.Join("\n - ", problems));

        return new ApiConfig
        {
            Port = Get("PORT").Length > 0 ? int.Parse(Get("PORT"), CultureInfo.InvariantCulture) : 3333,
            Production = production,
            DatabaseUrl = databaseUrl,
            MongoUrl = mongoUrl,
            MongoDbName = isTest ? "ecommerce_catalog_test" : Get("MONGODB_DB_NAME").Length > 0 ? Get("MONGODB_DB_NAME") : "ecommerce_catalog",
            MeiliUrl = meiliUrl,
            MeiliMasterKey = meiliMasterKey,
            MeiliIndexSuffix = isTest ? "_test" : "",
            RedisUrl = redisUrl,
            RedisKeyPrefix = isTest ? "ecom:test:" : "ecom:",
            RabbitmqUrl = rabbitmqUrl,
            MqPrefix = isTest ? "ecom.test." : "ecom.",
            AbandonedCartMinutes = abandoned,
            RateLimits = rateLimits,
            JwtAccessSecret = access,
            JwtRefreshSecret = refresh,
            RazorpayKeyId = razorpayKeyId,
            RazorpayKeySecret = razorpayKeySecret,
            RazorpayWebhookSecret = Get("RAZORPAY_WEBHOOK_SECRET"),
            RazorpayEnabled = razorpayKeyId.Length > 0,
            CorsOrigins = Get("CORS_ORIGINS").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            RateLimit = !isTest || Get("RATE_LIMIT") == "on",
        };
    }

    public static ApiConfig FromEnvironment()
    {
        var env = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = e.Value as string;
        return Load(env);
    }
}
