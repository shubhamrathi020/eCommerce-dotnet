using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EcomApi.Cache;
using EcomApi.Config;
using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services.Commerce;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

namespace EcomApi.Services.Messaging;

/// <summary>
/// Writes one outbox row. Callers use the same database context (and so the same transaction) as the domain write itself, so the two
/// either both commit or both roll back. There is no way to record an event without a matching write except the explicit standalone
/// form for the scheduler's jobs.
/// </summary>
public sealed class OutboxService(EcomDb db)
{
    /// <summary>Adds the row to the current unit of work; the caller's SaveChanges commits it with the domain write.</summary>
    public void Write(string routingKey, object payload) => db.OutboxEvents.Add(new OutboxEventRow { Id = Ids.NewId(), RoutingKey = routingKey, Payload = Json.Serialize(payload), CreatedAt = DateTime.UtcNow });

    /// <summary>For callers that are not already inside a transaction of their own: still one write, not paired with another.</summary>
    public async Task WriteStandaloneAsync(string routingKey, object payload)
    {
        Write(routingKey, payload);
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Real-time order tracking: Redis pub/sub, not RabbitMQ. A live, best-effort push to whatever browser tab is open on the order-tracking
/// page right now, not a durable event that must survive a broker restart.
/// </summary>
public sealed class OrderEventsService(RedisService redis, ApiConfig config)
{
    private string Channel(string orderId) => $"{config.RedisKeyPrefix}order-events:{orderId}";

    public async Task PublishAsync(Domain.Order order) => await redis.Subscriber.PublishAsync(RedisChannel.Literal(Channel(order.Id)), Json.Serialize(order));

    /// <summary>Calls <paramref name="send"/> with the current state immediately and then with every later change, until cancelled.</summary>
    public async Task ObserveAsync(string orderId, Domain.Order initial, Func<string, Task> send, CancellationToken ct)
    {
        await send(Json.Serialize(initial));
        var queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var channel = RedisChannel.Literal(Channel(orderId));
        var sub = redis.Subscriber;
        await sub.SubscribeAsync(channel, (_, message) => queue.Writer.TryWrite(message.ToString()));
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(ct)) await send(message);
        }
        catch (OperationCanceledException)
        {
            // the client went away
        }
        finally
        {
            await sub.UnsubscribeAsync(channel);
        }
    }
}

public sealed class DeadLetter
{
    public string RoutingKey { get; set; } = "";
    public JsonNode? Body { get; set; }
    public string? DeadAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// The one RabbitMQ connection the API uses for the outbox relay, the notification consumer and the scheduler's events. Declares the whole
/// topology once at boot: exchange, retry queue, dead-letter exchange and queue, and the notifications queue. Retry with backoff needs no
/// plugin: a message that failed processing is republished to the retry queue with a per-message expiration (2s, 8s, 20s), and that queue
/// dead-letters back to the events exchange when the TTL elapses. After the configured attempts the message goes to the dead-letter queue.
/// </summary>
public sealed class RabbitService(ApiConfig config)
{
    private IConnection? connection;
    private IChannel? confirmChannel;
    private IChannel? consumeChannel;
    private volatile bool connected;

    private string Name(string suffix) => $"{config.MqPrefix}{suffix}";
    public string Events => Name("events");
    public string Dlx => Name("dlx");
    public string Dlq => Name("q.dlq");
    public string RetryQueue => Name("q.retry");
    public string NotificationsQueue => Name("q.notifications");

    public async Task InitAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(config.RabbitmqUrl), AutomaticRecoveryEnabled = true };
        connection = await factory.CreateConnectionAsync();
        connection.ConnectionShutdownAsync += (_, _) =>
        {
            connected = false;
            return Task.CompletedTask;
        };
        connection.RecoverySucceededAsync += (_, _) =>
        {
            connected = true;
            return Task.CompletedTask;
        };
        confirmChannel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        consumeChannel = await connection.CreateChannelAsync();
        await DeclareTopologyAsync(confirmChannel);
        connected = true;
    }

    public async Task CloseAsync()
    {
        try
        {
            if (confirmChannel is not null) await confirmChannel.CloseAsync();
            if (consumeChannel is not null) await consumeChannel.CloseAsync();
            if (connection is not null) await connection.CloseAsync();
        }
        catch (Exception)
        {
            // shutting down
        }
    }

    /// <summary>For /readyz: the connection's own last known state (a round trip that fails can close the channel real work uses).</summary>
    public Task PingAsync() => connected ? Task.CompletedTask : throw new InvalidOperationException("RabbitMQ connection is not open");

    private async Task DeclareTopologyAsync(IChannel ch)
    {
        await ch.ExchangeDeclareAsync(Events, ExchangeType.Topic, durable: true);
        await ch.ExchangeDeclareAsync(Dlx, ExchangeType.Topic, durable: true);
        await ch.QueueDeclareAsync(Dlq, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueBindAsync(Dlq, Dlx, "#");
        // No queue-level TTL: each retry publish sets its own expiration for a per-attempt backoff instead.
        await ch.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = Events });
        await ch.QueueDeclareAsync(NotificationsQueue, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueBindAsync(NotificationsQueue, Events, "order.*");
        await ch.QueueBindAsync(NotificationsQueue, Events, "payment.*");
        await ch.QueueBindAsync(NotificationsQueue, Events, "cart.*");
    }

    private static BasicProperties Props(string? messageId, IDictionary<string, object?>? headers, int? expirationMs) => new()
    {
        Persistent = true,
        ContentType = "application/json",
        MessageId = messageId,
        Headers = headers,
        Expiration = expirationMs?.ToString(),
    };

    /// <summary>Publishes with a publisher confirm: completes only once the broker has acknowledged the message.</summary>
    public async Task PublishAsync(string routingKey, string json, string? messageId = null)
    {
        await confirmChannel!.BasicPublishAsync(Events, routingKey, mandatory: false, Props(messageId, null, null), Encoding.UTF8.GetBytes(json));
    }

    /// <summary>The same publish-with-confirm straight to one named queue (the retry queue, or the dead-letter queue).</summary>
    public async Task PublishToQueueAsync(string queue, string routingKey, string json, Dictionary<string, object?> headers, int? expirationMs = null)
    {
        headers["x-original-routing-key"] = routingKey;
        await confirmChannel!.BasicPublishAsync(string.Empty, queue, mandatory: false, Props(null, headers, expirationMs), Encoding.UTF8.GetBytes(json));
    }

    public async Task ConsumeAsync(string queue, ushort prefetch, Func<BasicDeliverEventArgs, Task> onMessage)
    {
        await consumeChannel!.BasicQosAsync(0, prefetch, false);
        var consumer = new AsyncEventingBasicConsumer(consumeChannel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                await onMessage(args);
            }
            catch (Exception error)
            {
                Log.Error("Rabbit", $"unhandled consumer error on {queue}: {error.Message}");
            }
        };
        await consumeChannel.BasicConsumeAsync(queue, autoAck: false, consumer);
    }

    public Task AckAsync(BasicDeliverEventArgs message) => consumeChannel!.BasicAckAsync(message.DeliveryTag, false).AsTask();

    private static string? HeaderString(IDictionary<string, object?>? headers, string key) => headers is not null && headers.TryGetValue(key, out var v) && v is not null ? v is byte[] bytes ? Encoding.UTF8.GetString(bytes) : v.ToString() : null;

    /// <summary>
    /// Non-destructive peek: pulls up to <paramref name="limit"/> messages off the queue and immediately nacks them back with requeue, so the
    /// dead-letter list can show what is there without consuming it. A concurrent peek can reorder the queue slightly.
    /// </summary>
    public async Task<List<DeadLetter>> PeekAsync(string queue, int limit)
    {
        var seen = new List<DeadLetter>();
        for (var i = 0; i < limit; i++)
        {
            var msg = await consumeChannel!.BasicGetAsync(queue, autoAck: false);
            if (msg is null) break;
            var headers = msg.BasicProperties.Headers;
            seen.Add(new DeadLetter
            {
                RoutingKey = HeaderString(headers, "x-original-routing-key") ?? msg.RoutingKey,
                Body = JsonNode.Parse(Encoding.UTF8.GetString(msg.Body.ToArray())),
                DeadAt = msg.BasicProperties.Timestamp.UnixTime > 0 ? Derive.Iso(DateTimeOffset.FromUnixTimeSeconds(msg.BasicProperties.Timestamp.UnixTime)) : null,
                RetryCount = int.TryParse(HeaderString(headers, "x-retry-count"), out var n) ? n : 0,
                LastError = HeaderString(headers, "x-last-error"),
            });
            await consumeChannel.BasicNackAsync(msg.DeliveryTag, false, true);
        }
        return seen;
    }

    /// <summary>Pops the single oldest message off the queue (permanently, acked) for the caller to republish elsewhere.</summary>
    public async Task<(string RoutingKey, string Body)?> PopOldestAsync(string queue)
    {
        var msg = await consumeChannel!.BasicGetAsync(queue, autoAck: false);
        if (msg is null) return null;
        await consumeChannel.BasicAckAsync(msg.DeliveryTag, false);
        return (HeaderString(msg.BasicProperties.Headers, "x-original-routing-key") ?? msg.RoutingKey, Encoding.UTF8.GetString(msg.Body.ToArray()));
    }
}

/// <summary>
/// Polls the outbox table for unpublished rows and publishes each to RabbitMQ. Guarded by a Redis lock (SET NX EX) so that with several API
/// replicas only whichever holds the lock for a tick queries and publishes. The lock is released right after the tick's work finishes; the
/// TTL is a crash-safety net. If the broker is down, the row's attempts and last error are recorded and published-at stays null, so the next
/// tick tries the same row again: no event is ever silently dropped, only delayed.
/// </summary>
public sealed class OutboxRelayService(IServiceScopeFactory scopes, RabbitService rabbit, RedisService redis) : BackgroundService
{
    private const int PollMs = 2_000;
    private const int LockTtlSeconds = 10;
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception error)
                {
                    Log.Error("OutboxRelay", $"tick failed: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task TickAsync()
    {
        var lockKey = redis.Key("lock:outbox-relay");
        if (!await redis.Db.StringSetAsync(lockKey, "1", TimeSpan.FromSeconds(LockTtlSeconds), When.NotExists)) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EcomDb>();
            var rows = await db.OutboxEvents.Where(e => e.PublishedAt == null).OrderBy(e => e.CreatedAt).Take(BatchSize).ToListAsync();
            foreach (var row in rows)
            {
                try
                {
                    await rabbit.PublishAsync(row.RoutingKey, row.Payload, row.Id);
                    row.PublishedAt = DateTime.UtcNow;
                    row.Attempts++;
                    await db.SaveChangesAsync();
                }
                catch (Exception error)
                {
                    row.Attempts++;
                    row.LastError = error.Message.Length > 500 ? error.Message[..500] : error.Message;
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (Exception)
                    {
                        // best effort
                    }
                    Log.Warn("OutboxRelay", $"publish failed for outbox event {row.Id} ({row.RoutingKey}): {error.Message}");
                }
            }
        }
        finally
        {
            try
            {
                await redis.Db.KeyDeleteAsync(lockKey);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}

/// <summary>
/// The one consumer of the notifications queue: turns an outbox event into an email via the dev mail service. Idempotent: each event's own
/// outbox-row id is the AMQP message id, and a Redis SET NX on that id is the dedupe check, so a redelivery is a no-op rather than a second email.
/// </summary>
public sealed class NotificationConsumerService(RabbitService rabbit, MailService mail, RedisService redis)
{
    /// <summary>Attempt 1, 2 and 3 wait 2s, 8s and 20s in the retry queue before coming back; the 4th failure goes to the dead-letter queue.</summary>
    private static readonly int[] RetryDelaysMs = [2_000, 8_000, 20_000];
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromDays(7);

    public Task StartAsync() => rabbit.ConsumeAsync(rabbit.NotificationsQueue, 5, HandleAsync);

    private async Task HandleAsync(BasicDeliverEventArgs msg)
    {
        var messageId = msg.BasicProperties.MessageId;
        if (!string.IsNullOrEmpty(messageId))
        {
            var isNew = await redis.Db.StringSetAsync(redis.Key($"dedupe:{messageId}"), "1", DedupeTtl, When.NotExists);
            if (!isNew)
            {
                await rabbit.AckAsync(msg);
                return;
            }
        }
        try
        {
            Deliver(msg.RoutingKey, JsonNode.Parse(Encoding.UTF8.GetString(msg.Body.ToArray()))!);
            await rabbit.AckAsync(msg);
        }
        catch (Exception error)
        {
            await RetryOrDeadLetterAsync(msg, error);
        }
    }

    private async Task RetryOrDeadLetterAsync(BasicDeliverEventArgs msg, Exception error)
    {
        var headers = msg.BasicProperties.Headers;
        var retryCount = headers is not null && headers.TryGetValue("x-retry-count", out var rc) && rc is not null ? Convert.ToInt32(rc) : 0;
        var routingKey = msg.RoutingKey;
        var body = Encoding.UTF8.GetString(msg.Body.ToArray());
        var lastError = error.Message.Length > 500 ? error.Message[..500] : error.Message;
        if (retryCount < RetryDelaysMs.Length)
        {
            Log.Warn("NotificationConsumer", $"{routingKey} failed (attempt {retryCount + 1}): {error.Message}; retrying in {RetryDelaysMs[retryCount]}ms");
            await rabbit.PublishToQueueAsync(rabbit.RetryQueue, routingKey, body, new Dictionary<string, object?> { ["x-retry-count"] = retryCount + 1, ["x-last-error"] = lastError }, RetryDelaysMs[retryCount]);
        }
        else
        {
            Log.Error("NotificationConsumer", $"{routingKey} failed permanently after {retryCount} retries: {error.Message}; moving to dead-letter queue");
            await rabbit.PublishToQueueAsync(rabbit.Dlq, routingKey, body, new Dictionary<string, object?> { ["x-retry-count"] = retryCount, ["x-last-error"] = lastError });
        }
        await rabbit.AckAsync(msg);
    }

    private void Deliver(string routingKey, JsonNode payload)
    {
        string S(string key) => payload[key]?.GetValue<string>() ?? "";
        Money? Amount(string key) => payload[key] is JsonNode n ? new Money(n["amount"]!.GetValue<long>()) : null;
        switch (routingKey)
        {
            case "order.placed":
            {
                var count = payload["itemCount"]!.GetValue<long>();
                var cod = payload["cod"]?.GetValue<bool>() ?? false;
                mail.Send(S("email"), $"Order confirmed: {S("orderId")}",
                    $"Hi {S("name")}, we've received your order {S("orderId")} ({count} item{(count == 1 ? "" : "s")}, {Money.Format(Amount("total")!)}){(cod ? " to be paid on delivery" : "")}. We'll email you again once it ships.",
                    $"/orders/{S("orderId")}");
                return;
            }
            case "order.cancelled":
                mail.Send(S("email"), $"Order cancelled: {S("orderId")}", $"Hi {S("name")}, your order {S("orderId")} has been cancelled{((payload["byStaff"]?.GetValue<bool>() ?? false) ? " by our team" : "")}. Any payment taken will be refunded.", $"/orders/{S("orderId")}");
                return;
            case "order.expired":
                mail.Send(S("email"), $"Order cancelled: {S("orderId")}", $"Hi {S("name")}, we cancelled order {S("orderId")} because payment wasn't completed in time. No charge was made; please place it again if you'd still like it.", $"/orders/{S("orderId")}");
                return;
            case "payment.confirmed":
                mail.Send(S("email"), $"Payment received for order {S("orderId")}", $"Hi {S("name")}, we've received your payment of {Money.Format(Amount("amount")!)} for order {S("orderId")}.", $"/orders/{S("orderId")}");
                return;
            case "payment.refunded":
                mail.Send(S("email"), $"Refund issued for order {S("orderId")}", $"Hi {S("name")}, we've issued a refund of {Money.Format(Amount("amount")!)} for order {S("orderId")}. It should reach your original payment method within a few business days.", $"/orders/{S("orderId")}");
                return;
            case "cart.abandoned":
            {
                var count = payload["itemCount"]!.GetValue<long>();
                mail.Send(S("email"), "You left something in your cart", $"Hi {S("name")}, you still have {count} item{(count == 1 ? "" : "s")} waiting in your cart. Come back and finish checking out whenever you're ready.", "/cart");
                return;
            }
            default:
                Log.Warn("NotificationConsumer", $"no handler for routing key \"{routingKey}\"; dropping (acked)");
                return;
        }
    }
}

/// <summary>
/// Scheduled jobs, active instead of "run on the next request". Each job is guarded by a Redis lock (SET NX EX) released right after the job
/// finishes, so with several API replicas only whichever wins the lock for a tick actually queries and writes. The TTL is only a crash-safety net.
/// </summary>
public sealed class SchedulerService(IServiceScopeFactory scopes, RedisService redis, ApiConfig config) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AbandonedInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweep = RunEvery(SweepInterval, "sweep-expired", 20, async scope => await scope.ServiceProvider.GetRequiredService<OrderService>().SweepExpiredAsync(), "reservation-expiry sweep", stoppingToken);
        var abandoned = RunEvery(AbandonedInterval, "abandoned-cart", 60, scope => RemindAbandonedCartsAsync(scope.ServiceProvider), "abandoned-cart reminder", stoppingToken);
        await Task.WhenAll(sweep, abandoned);
    }

    private async Task RunEvery(TimeSpan interval, string name, int crashSafetyTtlSeconds, Func<IServiceScope, Task> job, string label, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await WithLockAsync(name, crashSafetyTtlSeconds, job);
                }
                catch (Exception error)
                {
                    Log.Error("Scheduler", $"{label} failed: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task WithLockAsync(string name, int ttlSeconds, Func<IServiceScope, Task> job)
    {
        var key = redis.Key($"lock:{name}");
        if (!await redis.Db.StringSetAsync(key, "1", TimeSpan.FromSeconds(ttlSeconds), When.NotExists)) return;
        try
        {
            using var scope = scopes.CreateScope();
            await job(scope);
        }
        finally
        {
            try
            {
                await redis.Db.KeyDeleteAsync(key);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// One reminder email per cart per "abandoned" episode: a signed-in customer's cart that has sat untouched, non-empty, for the configured
    /// minutes, and has not already been reminded since its last real change. Guest carts are skipped: there is no email to send to yet.
    /// </summary>
    public async Task RemindAbandonedCartsAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<EcomDb>();
        var outbox = services.GetRequiredService<OutboxService>();
        var cutoff = DateTime.UtcNow.AddMinutes(-config.AbandonedCartMinutes);
        var candidates = await db.Carts.Where(c => c.UpdatedAt < cutoff && c.ReminderSentAt == null && c.OwnerKey.StartsWith("user:") && c.Items != "[]").Take(50).ToListAsync();
        foreach (var cart in candidates)
        {
            var userId = cart.OwnerKey["user:".Length..];
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) continue;
            var items = Json.Deserialize<List<StoredItem>>(cart.Items) ?? [];
            var itemCount = items.Sum(i => i.Quantity);
            if (itemCount == 0) continue;
            outbox.Write("cart.abandoned", new { email = user.Email, name = user.Name, itemCount });
            cart.ReminderSentAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
