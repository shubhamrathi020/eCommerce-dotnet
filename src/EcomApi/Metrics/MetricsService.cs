using EcomApi.Cache;
using EcomApi.Data;
using EcomApi.Services.Catalog;
using EcomApi.Services.Commerce;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace EcomApi.Metrics;

/// <summary>
/// Prometheus metrics: the RED method (rate, errors, duration) for every HTTP request, plus the operational numbers this project already
/// tracks internally (cache hit ratio, circuit-breaker state, outbox backlog) republished in a format Prometheus can scrape. Gauges that
/// read another service are computed at scrape time, so nothing is pushed or duplicated.
/// </summary>
public sealed class MetricsService
{
    private static readonly Dictionary<string, int> CircuitStateValue = new() { ["closed"] = 0, ["half_open"] = 1, ["open"] = 2 };
    private readonly Histogram duration;
    private readonly Counter requests;

    public MetricsService(CacheService cache, RazorpayService razorpay, SearchService search, IServiceScopeFactory scopes)
    {
        duration = Prometheus.Metrics.CreateHistogram("http_request_duration_seconds", "HTTP request duration in seconds",
            new HistogramConfiguration { LabelNames = ["method", "route", "status"], Buckets = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5] });
        requests = Prometheus.Metrics.CreateCounter("http_requests_total",
            "Total HTTP requests (rate and error-rate PromQL both derive from this: e.g. rate(http_requests_total{status=~\"5..\"}[5m]))",
            new CounterConfiguration { LabelNames = ["method", "route", "status"] });
        var hitRatio = Prometheus.Metrics.CreateGauge("cache_hit_ratio", "Catalog cache hit ratio since process start");
        var breakerState = Prometheus.Metrics.CreateGauge("circuit_breaker_state", "Circuit breaker state per dependency: 0=closed, 1=half_open, 2=open",
            new GaugeConfiguration { LabelNames = ["dependency"] });
        var unpublished = Prometheus.Metrics.CreateGauge("outbox_unpublished_total",
            "Outbox events not yet relayed to RabbitMQ: should stay near 0; a sustained rise means the relay or broker is stuck");
        // The runtime's own baseline (memory, CPU, start time), under the conventional process_* names.
        var resident = Prometheus.Metrics.CreateGauge("process_resident_memory_bytes", "Resident memory size in bytes.");
        var virtualMemory = Prometheus.Metrics.CreateGauge("process_virtual_memory_bytes", "Virtual memory size in bytes.");
        var cpu = Prometheus.Metrics.CreateCounter("process_cpu_seconds_total", "Total user and system CPU time spent in seconds.");
        var started = Prometheus.Metrics.CreateGauge("process_start_time_seconds", "Start time of the process since unix epoch in seconds.");
        var process = System.Diagnostics.Process.GetCurrentProcess();
        started.Set(new DateTimeOffset(process.StartTime).ToUnixTimeSeconds());
        Prometheus.Metrics.DefaultRegistry.AddBeforeCollectCallback(async ct =>
        {
            process.Refresh();
            resident.Set(process.WorkingSet64);
            virtualMemory.Set(process.VirtualMemorySize64);
            cpu.IncTo(process.TotalProcessorTime.TotalSeconds);
            var stats = cache.GetStats();
            var total = stats.Hits + stats.Misses;
            hitRatio.Set(total == 0 ? 0 : (double)stats.Hits / total);
            foreach (var s in new[] { razorpay.BreakerStats(), search.BreakerStats() }) breakerState.WithLabels(s.Name).Set(CircuitStateValue.GetValueOrDefault(s.State, -1));
            try
            {
                using var scope = scopes.CreateScope();
                unpublished.Set(await scope.ServiceProvider.GetRequiredService<EcomDb>().OutboxEvents.CountAsync(e => e.PublishedAt == null, ct));
            }
            catch (Exception)
            {
                unpublished.Set(-1);
            }
        });
    }

    /// <summary>The matched path pattern is the route label, never the raw URL: one time series per order id would grow forever.</summary>
    public void RecordHttpRequest(string method, string route, int status, double seconds)
    {
        duration.WithLabels(method, route, status.ToString()).Observe(seconds);
        requests.WithLabels(method, route, status.ToString()).Inc();
    }

    public async Task<string> RenderAsync()
    {
        using var ms = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
