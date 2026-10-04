using System.Text.Json.Nodes;
using EcomApi.Cache;
using EcomApi.Config;
using EcomApi.Data;
using EcomApi.Http;
using EcomApi.Metrics;
using EcomApi.Services;
using EcomApi.Services.Catalog;
using EcomApi.Services.Commerce;
using EcomApi.Services.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EcomApi.Controllers;

public sealed class HealthController(EcomDb db, MongoService mongo, SearchService search, RedisService redis, RabbitService rabbit) : ApiControllerBase
{
    /// <summary>The process is up. Never touches dependencies, so an outage does not restart every pod.</summary>
    [HttpGet("healthz"), SkipThrottle]
    public IActionResult Healthz() => Text("ok");

    /// <summary>Ready for traffic only when every store the API depends on answers.</summary>
    [HttpGet("readyz"), SkipThrottle]
    public async Task<IActionResult> Readyz()
    {
        try
        {
            await Task.WhenAll(db.Database.ExecuteSqlRawAsync("SELECT 1"), mongo.PingAsync(), search.HealthAsync(), redis.PingAsync(), rabbit.PingAsync());
            return Text("ok");
        }
        catch (Exception)
        {
            throw new HttpError(503, "a dependency is unavailable");
        }
    }
}

/// <summary>Development only: the mails the API would have sent (verification and reset links). 404 in production.</summary>
[Route("dev")]
public sealed class DevOutboxController(MailService mail, ApiConfig config) : ApiControllerBase
{
    [HttpGet("outbox"), SkipThrottle]
    public IActionResult Outbox()
    {
        if (config.Production) throw new HttpError(404, "Not found");
        return Ok(mail.List());
    }
}

/// <summary>Prometheus scrapes this: internal and unauthenticated, kept off the public internet at the network layer.</summary>
public sealed class MetricsController(MetricsService metrics) : ApiControllerBase
{
    [HttpGet("metrics")]
    public async Task<IActionResult> Get() => new ApiResult(200, null, await metrics.RenderAsync(), "text/plain; charset=utf-8; version=0.0.4");
}

public sealed class SystemController(CacheService cache, RazorpayService razorpay, SearchService search, RabbitService rabbit) : ApiControllerBase
{
    [HttpGet("admin/system/cache-stats"), RequireAuth("system:read")]
    public IActionResult CacheStats()
    {
        var stats = cache.GetStats();
        var total = stats.Hits + stats.Misses;
        return Ok(new
        {
            hits = stats.Hits,
            misses = stats.Misses,
            coalesced = stats.Coalesced,
            rateLimitBlocked = stats.RateLimitBlocked,
            hitRatio = total == 0 ? 0 : Math.Round((double)stats.Hits / total * 100) / 100,
        });
    }

    /// <summary>Circuit-breaker state for the two external dependencies: "closed" is normal, "open" means calls fail fast.</summary>
    [HttpGet("admin/system/resilience"), RequireAuth("system:read")]
    public IActionResult Resilience() => Ok(new { circuitBreakers = new[] { razorpay.BreakerStats(), search.BreakerStats() } });

    [HttpGet("admin/system/dead-letters"), RequireAuth("system:read")]
    public async Task<IActionResult> DeadLetters()
    {
        var parsed = Request.Query.TryGetValue("limit", out var raw) && double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n != 0 ? (int)n : 20;
        return Ok(await rabbit.PeekAsync(rabbit.Dlq, Math.Min(100, parsed)));
    }

    [HttpPost("admin/system/dead-letters/replay"), RequireAuth("system:write")]
    public async Task<IActionResult> ReplayDeadLetter()
    {
        var popped = await rabbit.PopOldestAsync(rabbit.Dlq);
        if (popped is null) return Ok(new { replayed = false });
        await rabbit.PublishAsync(popped.Value.RoutingKey, popped.Value.Body);
        return Ok(new { replayed = true, routingKey = popped.Value.RoutingKey });
    }
}
