using EcomApi.Services;

namespace EcomApi.Tests;

public class ResilienceTests
{
    private static CircuitBreaker Breaker(int threshold, int cooldownMs, int timeoutMs) => new("test", new CircuitBreakerOptions(threshold, cooldownMs, timeoutMs));

    [Fact]
    public async Task StaysClosedAndPassesResultsThroughWhileCallsSucceed()
    {
        var breaker = Breaker(3, 1000, 100);
        Assert.Equal("ok", await breaker.ExecAsync(() => Task.FromResult("ok")));
        Assert.Equal("closed", breaker.Stats().State);
    }

    [Fact]
    public async Task OpensAfterTheConfiguredConsecutiveFailuresThenRejectsWithoutCallingAgain()
    {
        var breaker = Breaker(2, 1000, 100);
        Task<int> Fail() => Task.FromException<int>(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => breaker.ExecAsync(Fail));
        Assert.Equal("closed", breaker.Stats().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => breaker.ExecAsync(Fail));
        Assert.Equal("open", breaker.Stats().State);
        var called = false;
        await Assert.ThrowsAsync<CircuitOpenException>(() => breaker.ExecAsync(() => { called = true; return Fail(); }));
        Assert.False(called);
    }

    [Fact]
    public async Task ACallThatNeverSettlesCountsAsAFailureViaTheTimeout()
    {
        var breaker = Breaker(1, 1000, 30);
        await Assert.ThrowsAsync<CallTimeoutException>(() => breaker.ExecAsync(() => new TaskCompletionSource<int>().Task));
        Assert.Equal("open", breaker.Stats().State);
    }

    [Fact]
    public async Task AfterTheCooldownOneProbeIsAllowedAndSuccessClosesTheCircuit()
    {
        var breaker = Breaker(1, 20, 100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => breaker.ExecAsync<int>(() => throw new InvalidOperationException("down")));
        await Task.Delay(40);
        Assert.Equal("recovered", await breaker.ExecAsync(() => Task.FromResult("recovered")));
        Assert.Equal("closed", breaker.Stats().State);
    }

    [Fact]
    public async Task AFailedProbeReopensTheCircuitForAnotherFullCooldown()
    {
        var breaker = Breaker(1, 50, 100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => breaker.ExecAsync<int>(() => throw new InvalidOperationException("down")));
        await Task.Delay(70);
        await Assert.ThrowsAsync<InvalidOperationException>(() => breaker.ExecAsync<int>(() => throw new InvalidOperationException("still down")));
        Assert.Equal("open", breaker.Stats().State);
        await Assert.ThrowsAsync<CircuitOpenException>(() => breaker.ExecAsync(() => Task.FromResult(1)));
    }

    [Fact]
    public async Task RetryReturnsTheFirstSuccessRetriesBlipsAndGivesUpAfterTheLastAttempt()
    {
        var calls = 0;
        Assert.Equal("ok", await Retry.WithRetryAsync(() => { calls++; return Task.FromResult("ok"); }, 3, 5));
        Assert.Equal(1, calls);

        calls = 0;
        Assert.Equal("ok", await Retry.WithRetryAsync(() => ++calls < 2 ? throw new InvalidOperationException("blip") : Task.FromResult("ok"), 3, 5));
        Assert.Equal(2, calls);

        calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.WithRetryAsync<string>(() => { calls++; throw new InvalidOperationException("down for good"); }, 3, 5));
        Assert.Equal("down for good", error.Message);
        Assert.Equal(3, calls);
    }
}
