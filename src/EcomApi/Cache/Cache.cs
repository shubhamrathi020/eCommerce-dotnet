using System.Collections.Concurrent;
using System.Text.Json;
using EcomApi.Config;
using EcomApi.Http;
using StackExchange.Redis;

namespace EcomApi.Cache;

/// <summary>The one Redis connection the API uses for caching, rate limits, locks and live order updates.</summary>
public sealed class RedisService(ApiConfig config) : IAsyncDisposable
{
    private ConnectionMultiplexer? mux;

    public string Prefix => config.RedisKeyPrefix;

    public async Task InitAsync()
    {
        var options = ConfigurationOptions.Parse(ToRedisConfiguration(config.RedisUrl));
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        mux = await ConnectionMultiplexer.ConnectAsync(options);
        await Db.PingAsync();
    }

    private static string ToRedisConfiguration(string url)
    {
        if (!url.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase)) return url;
        var uri = new Uri(url);
        var s = $"{uri.Host}:{(uri.IsDefaultPort ? 6379 : uri.Port)}";
        if (uri.Scheme == "rediss") s += ",ssl=true";
        var password = uri.UserInfo.Contains(':') ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..]) : "";
        if (password.Length > 0) s += ",password=" + password;
        var dbIndex = uri.AbsolutePath.Trim('/');
        if (dbIndex.Length > 0) s += ",defaultDatabase=" + dbIndex;
        return s;
    }

    public IDatabase Db => (mux ?? throw new InvalidOperationException("Redis is not connected")).GetDatabase();

    public ISubscriber Subscriber => (mux ?? throw new InvalidOperationException("Redis is not connected")).GetSubscriber();

    /// <summary>Every key this app touches is namespaced, so a test run (a different prefix) never collides with development data.</summary>
    public RedisKey Key(string key) => Prefix + key;

    public async Task PingAsync() => await Db.PingAsync();

    public async ValueTask DisposeAsync()
    {
        if (mux is not null) await mux.CloseAsync();
    }
}

public sealed class CacheStats
{
    public long Hits { get; set; }
    public long Misses { get; set; }
    /// <summary>Concurrent callers for the same key coalesced onto one in-flight fetch instead of each hitting the backend.</summary>
    public long Coalesced { get; set; }
    /// <summary>Requests refused with 429, by rate-limit bucket.</summary>
    public Dictionary<string, long> RateLimitBlocked { get; set; } = new();
}

/// <summary>
/// Cache-aside reads with tagged invalidation and stampede protection. Never used for anything personalised or private: only catalog
/// reads, which are the same for every shopper. Stampede protection is a per-process in-flight map: two requests on this instance
/// for the same key share one backend fetch.
/// </summary>
public sealed class CacheService(RedisService redis)
{
    private readonly ConcurrentDictionary<string, Task<object?>> inFlight = new();
    private long hits;
    private long misses;
    private long coalesced;
    private readonly ConcurrentDictionary<string, long> blocked = new();

    public CacheStats GetStats() => new() { Hits = Interlocked.Read(ref hits), Misses = Interlocked.Read(ref misses), Coalesced = Interlocked.Read(ref coalesced), RateLimitBlocked = new Dictionary<string, long>(blocked) };

    public void RecordRateLimitBlocked(string bucket) => blocked.AddOrUpdate(bucket, 1, (_, n) => n + 1);

    /// <summary>
    /// Returns the cached value for the key, or computes it with the factory, caches it for the TTL and records it under every tag
    /// (so InvalidateTagAsync can find and drop it later). Tags can depend on the computed value itself.
    /// </summary>
    public async Task<T> GetOrSetAsync<T>(string key, int ttlSeconds, Func<T, IEnumerable<string>> tags, Func<Task<T>> factory)
    {
        // A Redis outage degrades to "always compute, never cache": a failed read is treated as a miss.
        RedisValue cached = RedisValue.Null;
        try
        {
            cached = await redis.Db.StringGetAsync(redis.Key(key));
        }
        catch (Exception)
        {
            // fall through to compute
        }
        if (cached.HasValue)
        {
            Interlocked.Increment(ref hits);
            return JsonSerializer.Deserialize<T>((string)cached!, Json.Web)!;
        }

        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        while (!inFlight.TryAdd(key, pending.Task))
        {
            if (inFlight.TryGetValue(key, out var existing))
            {
                Interlocked.Increment(ref coalesced);
                return (T)(await existing)!;
            }
        }

        Interlocked.Increment(ref misses);
        try
        {
            var value = await factory();
            // Best-effort: a Redis write failure degrades to "computed but not cached".
            try
            {
                var batch = redis.Db.CreateBatch();
                var tasks = new List<Task> { batch.StringSetAsync(redis.Key(key), JsonSerializer.Serialize(value, Json.Web), TimeSpan.FromSeconds(ttlSeconds)) };
                foreach (var tag in tags(value)) tasks.Add(batch.SetAddAsync(redis.Key($"tag:{tag}"), key));
                batch.Execute();
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // Swallow: the caller still gets the freshly computed value.
            }
            pending.SetResult(value);
            return value;
        }
        catch (Exception error)
        {
            pending.SetException(error);
            _ = pending.Task.Exception; // observed here; waiting callers see the same failure
            throw;
        }
        finally
        {
            inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Drops every cache entry ever tagged with the tag (called after any catalog write).</summary>
    public async Task InvalidateTagAsync(string tag)
    {
        try
        {
            var tagKey = redis.Key($"tag:{tag}");
            var members = await redis.Db.SetMembersAsync(tagKey);
            if (members.Length == 0) return;
            var keys = members.Select(m => redis.Key((string)m!)).Append(tagKey).ToArray();
            await redis.Db.KeyDeleteAsync(keys);
        }
        catch (Exception error)
        {
            // Best-effort: a cache that fails to invalidate goes stale until its TTL expires, never blocks the write itself.
            Log.Warn("Cache", $"could not invalidate tag {tag}: {error.Message}");
        }
    }
}

/// <summary>
/// Coupons with a hard cap on total redemptions, backed by an atomic Redis counter so concurrent checkouts can never exceed the cap.
/// </summary>
public sealed class CouponRedemptionService(RedisService redis)
{
    private static readonly Dictionary<string, int> Capped = new() { ["WELCOME10"] = 500 };

    private RedisKey Key(string code) => redis.Key($"coupon:redemptions:{code.ToUpperInvariant()}");

    public int? CapFor(string code) => Capped.TryGetValue(code.ToUpperInvariant(), out var cap) ? cap : null;

    /// <summary>Atomically claims one redemption slot; the one caller that pushes the counter over the cap gives its slot straight back.</summary>
    public async Task<bool> TryClaimAsync(string code)
    {
        var cap = CapFor(code);
        if (cap is null) return true;
        var count = await redis.Db.StringIncrementAsync(Key(code));
        if (count > cap)
        {
            await redis.Db.StringDecrementAsync(Key(code));
            return false;
        }
        return true;
    }

    /// <summary>Releases a claimed slot (an order using this coupon was cancelled).</summary>
    public async Task ReleaseAsync(string code)
    {
        if (CapFor(code) is null) return;
        await redis.Db.StringDecrementAsync(Key(code));
    }
}

/// <summary>
/// A plain fixed-window counter per client IP, per bucket, per window. The limits are read from the configuration at request time.
/// Fails open on a Redis outage (a rate limiter that takes the site down on a cache blip is worse than a brief lapse in limiting).
/// </summary>
public sealed class RateLimitGuard(ApiConfig config, RedisService redis, CacheService cache)
{
    public async Task CheckAsync(string bucket, HttpContext ctx)
    {
        if (!config.RateLimit) return;
        var (limit, windowSeconds) = config.RateLimits[bucket];
        var windowStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / windowSeconds * windowSeconds;
        var ip = ctx.ClientIp();
        var key = redis.Key($"ratelimit:{bucket}:{windowStart}:{ip}");
        long count;
        try
        {
            count = await redis.Db.StringIncrementAsync(key);
            if (count == 1) await redis.Db.KeyExpireAsync(key, TimeSpan.FromSeconds(windowSeconds));
        }
        catch (Exception)
        {
            return; // Redis unavailable: fail open rather than block all traffic on a cache outage.
        }
        if (count > limit)
        {
            cache.RecordRateLimitBlocked(bucket);
            ctx.Response.Headers["Retry-After"] = (windowStart + windowSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
            throw new HttpError(429, "Too many requests. Please wait a moment and try again.");
        }
    }
}

/// <summary>In-memory fixed-window limiter, one window per key.</summary>
public sealed class Throttler : IDisposable
{
    private sealed class Window(long resetAt)
    {
        public int Count;
        public long ResetAt = resetAt;
    }

    private readonly ConcurrentDictionary<string, Window> windows = new();
    private readonly Timer sweeper;

    public Throttler() => sweeper = new Timer(_ =>
    {
        var now = Environment.TickCount64;
        foreach (var (key, w) in windows)
            if (w.ResetAt <= now) windows.TryRemove(key, out Window? _);
    }, null, 60_000, 60_000);

    public bool Allow(string key, int limit, int ttlMs, out int retryAfterSeconds)
    {
        var now = Environment.TickCount64;
        var w = windows.AddOrUpdate(key, _ => new Window(now + ttlMs) { Count = 1 }, (_, current) =>
        {
            if (current.ResetAt <= now) return new Window(now + ttlMs) { Count = 1 };
            Interlocked.Increment(ref current.Count);
            return current;
        });
        retryAfterSeconds = (int)Math.Ceiling((w.ResetAt - now) / 1000.0);
        return w.Count <= limit;
    }

    public void Dispose() => sweeper.Dispose();
}
