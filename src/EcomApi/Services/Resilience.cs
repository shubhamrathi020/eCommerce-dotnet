namespace EcomApi.Services;

public sealed class CircuitOpenException(string name) : Exception($"circuit \"{name}\" is open");

public sealed class CallTimeoutException(string name, int ms) : Exception($"\"{name}\" timed out after {ms}ms");

public sealed class CircuitStats
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "closed";
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public string? OpenedAt { get; set; }
}

public sealed record CircuitBreakerOptions(int FailureThreshold, int CooldownMs, int TimeoutMs);

/// <summary>
/// A circuit breaker around calls to one external dependency (Razorpay, Meilisearch): timeout every call, count consecutive failures,
/// and once too many happen in a row stop even trying for a cooldown period, so a struggling dependency is not also hammered while it
/// recovers and callers fail fast instead of piling up behind a slow one. States: closed, open (calls rejected immediately), and after
/// the cooldown half_open (exactly one probe call is let through; success closes the circuit, failure reopens it).
/// </summary>
public sealed class CircuitBreaker(string name, CircuitBreakerOptions options)
{
    private readonly object gate = new();
    private string state = "closed";
    private int consecutiveFailures;
    private long openedAt;
    private string? lastError;
    private bool probeInFlight;

    public CircuitStats Stats()
    {
        lock (gate)
        {
            return new CircuitStats
            {
                Name = name,
                State = state,
                ConsecutiveFailures = consecutiveFailures,
                LastError = lastError,
                OpenedAt = state == "open" ? Domain.Derive.Iso(DateTimeOffset.FromUnixTimeMilliseconds(openedAt)) : null,
            };
        }
    }

    public async Task<T> ExecAsync<T>(Func<Task<T>> fn)
    {
        lock (gate)
        {
            if (state == "open")
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - openedAt < options.CooldownMs) throw new CircuitOpenException(name);
                // Cooldown elapsed: let exactly one caller probe the dependency; everyone else is still rejected until it settles.
                if (probeInFlight) throw new CircuitOpenException(name);
                state = "half_open";
                probeInFlight = true;
            }
        }
        try
        {
            var task = fn();
            if (await Task.WhenAny(task, Task.Delay(options.TimeoutMs)) != task) throw new CallTimeoutException(name, options.TimeoutMs);
            var result = await task;
            lock (gate)
            {
                consecutiveFailures = 0;
                lastError = null;
                state = "closed";
            }
            return result;
        }
        catch (Exception error)
        {
            lock (gate)
            {
                consecutiveFailures++;
                lastError = error.Message;
                if (state == "half_open" || consecutiveFailures >= options.FailureThreshold)
                {
                    state = "open";
                    openedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            throw;
        }
        finally
        {
            lock (gate) probeInFlight = false;
        }
    }
}

public static class Retry
{
    /// <summary>
    /// Retries the call up to <paramref name="attempts"/> times with exponential backoff, for a call that might have failed on a transient
    /// blip. A retry happens inside one circuit-breaker attempt (a blip counts as one failure toward the breaker), never the other way round.
    /// </summary>
    public static async Task<T> WithRetryAsync<T>(Func<Task<T>> fn, int attempts, int baseDelayMs)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return await fn();
            }
            catch (Exception error)
            {
                last = error;
                if (attempt < attempts - 1) await Task.Delay(baseDelayMs * (1 << attempt));
            }
        }
        throw last!;
    }
}
