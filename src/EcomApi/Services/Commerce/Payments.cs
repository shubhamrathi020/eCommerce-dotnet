using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EcomApi.Config;
using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services.Messaging;
using Microsoft.EntityFrameworkCore;

namespace EcomApi.Services.Commerce;

/// <summary>
/// Real Razorpay integration, test mode only, gated on your own keys. With no keys set, the API still starts and cash-on-delivery still
/// works; initiating an online payment refuses with a clear message. Every provider call is timed out, retried once on a transient blip,
/// and counted by a circuit breaker: after 3 consecutive failures the breaker opens for 30 seconds and calls fail fast with a clear
/// "temporarily unavailable" message. Cash on delivery is a completely separate code path, so a Razorpay outage never blocks it.
/// </summary>
public sealed class RazorpayService
{
    private readonly ApiConfig config;
    private readonly HttpClient? http;
    private readonly CircuitBreaker breaker = new("razorpay", new CircuitBreakerOptions(3, 30_000, 8_000));

    public RazorpayService(ApiConfig config)
    {
        this.config = config;
        if (!config.RazorpayEnabled) return;
        http = new HttpClient { BaseAddress = new Uri("https://api.razorpay.com/v1/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.RazorpayKeyId}:{config.RazorpayKeySecret}")));
    }

    public bool Enabled => http is not null;

    public CircuitStats BreakerStats() => breaker.Stats();

    private HttpClient RequireClient() => http ?? throw new AppError(ApiErrorCode.Validation, "Online payment is not set up on this server yet. Please choose cash on delivery, or ask the site owner to add Razorpay test keys.");

    /// <summary>Every failure surfaces as the same friendly error, never the raw provider exception.</summary>
    private async Task<T> GuardedAsync<T>(Func<Task<T>> fn)
    {
        try
        {
            return await breaker.ExecAsync(() => Retry.WithRetryAsync(fn, 2, 300));
        }
        catch (Exception error)
        {
            if (error is not CircuitOpenException) Log.Warn("RazorpayService", $"razorpay call failed: {error.Message}");
            throw new AppError(ApiErrorCode.Network, "Online payment is temporarily unavailable. Please choose cash on delivery, or try again in a minute.");
        }
    }

    private static async Task<JsonNode> PostAsync(HttpClient client, string path, JsonObject body)
    {
        using var response = await client.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Razorpay {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>Creates the provider-side order the checkout widget needs. The receipt is our own order id.</summary>
    public async Task<string> CreateOrderAsync(long amountPaise, string receipt)
    {
        var client = RequireClient();
        var order = await GuardedAsync(() => PostAsync(client, "orders", new JsonObject { ["amount"] = amountPaise, ["currency"] = "INR", ["receipt"] = receipt }));
        return order["id"]!.GetValue<string>();
    }

    /// <summary>
    /// The signature the checkout widget hands back after a successful payment: HMAC-SHA256 of "&lt;order_id&gt;|&lt;payment_id&gt;" keyed with the
    /// account secret. Verified server-side with a constant-time comparison; the secret never reaches the browser.
    /// </summary>
    public bool VerifyPaymentSignature(string providerOrderId, string providerPaymentId, string signature)
    {
        if (config.RazorpayKeySecret.Length == 0) return false;
        return SafeEqual(Hmac(config.RazorpayKeySecret, $"{providerOrderId}|{providerPaymentId}"), signature);
    }

    /// <summary>Webhook calls carry an HMAC-SHA256 of the raw request body keyed with the separate webhook secret set in the dashboard.</summary>
    public bool VerifyWebhookSignature(string rawBody, string signature)
    {
        if (config.RazorpayWebhookSecret.Length == 0) return false;
        return SafeEqual(Hmac(config.RazorpayWebhookSecret, rawBody), signature);
    }

    /// <summary>Refunds a captured payment; no amount refunds in full.</summary>
    public async Task RefundAsync(string providerPaymentId, long? amountPaise = null)
    {
        var client = RequireClient();
        var body = new JsonObject();
        if (amountPaise is not null) body["amount"] = amountPaise;
        await GuardedAsync(() => PostAsync(client, $"payments/{providerPaymentId}/refund", body));
    }

    private static string Hmac(string secret, string data) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data)));

    private static bool SafeEqual(string expected, string actual)
    {
        try
        {
            var a = Convert.FromHexString(expected);
            var b = Convert.FromHexString(actual);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class PaymentResultBody
{
    public required string ProviderPaymentId { get; set; }
    public required string ProviderOrderId { get; set; }
    public required string Signature { get; set; }
}

/// <summary>
/// Payments: initiate creates the Razorpay order the checkout widget needs; confirm verifies the signature server-side before an order is ever
/// marked paid. The amount always comes from the order the server already priced, never from anything the client sends.
/// </summary>
public sealed class PaymentService(EcomDb db, OrderService orders, CartService cart, InventoryService inventory, RazorpayService razorpay, OutboxService outbox, OrderEventsService orderEvents, ApiConfig config)
{
    public async Task<PaymentSession> InitiateAsync(string orderId, OwnerContext owner)
    {
        await orders.SweepExpiredAsync();
        var row = await orders.GetRowForOwnerAsync(orderId, owner);
        if (row.Status != "pending_payment") throw new AppError(ApiErrorCode.Validation, "This order does not need a payment.");
        if (row.PaymentStatus == "failed")
        {
            // A previous attempt released the stock hold; reclaim it before letting them try again.
            await inventory.TakeAsync(OrderService.StockLinesOf(row));
            row.PaymentStatus = "pending";
            row.PaymentDeadline = DateTime.UtcNow.AddMinutes(OrderService.PaymentWindowMinutes);
            await db.SaveChangesAsync();
        }
        var amount = Json.Deserialize<CartTotals>(row.Totals)!.Total.Amount;
        var providerOrderId = await razorpay.CreateOrderAsync(amount, row.Id);
        row.ProviderOrderId = providerOrderId;
        await db.SaveChangesAsync();
        return new PaymentSession { OrderId = orderId, ProviderOrderId = providerOrderId, KeyId = config.RazorpayKeyId, Amount = amount, Currency = "INR" };
    }

    public async Task<Order> ConfirmAsync(string orderId, OwnerContext owner, PaymentResultBody result)
    {
        await orders.SweepExpiredAsync();
        var row = await orders.GetRowForOwnerAsync(orderId, owner);
        if (row.PaymentStatus == "paid") return OrderService.ToOrder(row);
        if (row.Status == "cancelled") throw new AppError(ApiErrorCode.Validation, "The payment window for this order expired and its items were released. Please place the order again.");
        if (row.Status != "pending_payment") throw new AppError(ApiErrorCode.Validation, "This order can no longer be paid.");
        var valid = result.ProviderOrderId == row.ProviderOrderId && razorpay.VerifyPaymentSignature(result.ProviderOrderId, result.ProviderPaymentId, result.Signature);
        if (!valid) throw new AppError(ApiErrorCode.Validation, "Payment verification failed. If money was deducted it will be refunded.");
        var contract = await MarkPaidAsync(row, result.ProviderPaymentId);
        await cart.ReplaceWithEmptyAsync(Cookies.OwnerKeyFor(owner.UserId, owner.GuestToken ?? ""), row.ShippingMethod);
        return contract;
    }

    /// <summary>
    /// Marks an order paid and confirmed once, however many times the browser and the webhook report the same payment: the row is locked
    /// and re-read inside the transaction, and only the report that actually changes it writes the confirmation email and the live event.
    /// </summary>
    async Task<Order> MarkPaidAsync(OrderRow row, string providerPaymentId, OrderNote? note = null)
    {
        bool changed;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE \"id\" = {row.Id} FOR UPDATE");
            await db.Entry(row).ReloadAsync();
            changed = row.PaymentStatus != "paid";
            if (changed)
            {
                var now = Derive.Iso(DateTimeOffset.UtcNow);
                var timeline = Json.Deserialize<List<TimelineEntry>>(row.Timeline)!;
                timeline.Add(new TimelineEntry { Status = "paid", Label = "Payment received", At = now });
                timeline.Add(new TimelineEntry { Status = "confirmed", Label = "Order confirmed", At = now });
                row.Status = "confirmed";
                row.PaymentStatus = "paid";
                row.ProviderPaymentId = providerPaymentId;
                row.Timeline = Json.Serialize(timeline);
                if (note is not null)
                {
                    var notes = Json.Deserialize<List<OrderNote>>(row.Notes) ?? [];
                    notes.Insert(0, note);
                    row.Notes = Json.Serialize(notes);
                }
                outbox.Write("payment.confirmed", new { orderId = row.Id, email = row.ContactEmail, name = row.ContactName, amount = Json.Deserialize<CartTotals>(row.Totals)!.Total });
                await db.SaveChangesAsync();
            }
            await tx.CommitAsync();
        }
        var contract = OrderService.ToOrder(row);
        if (changed) await orderEvents.PublishAsync(contract);
        return contract;
    }

    static OrderNote SystemNote(string text) =>
        new() { Id = $"note_{Ids.Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}", At = Derive.Iso(DateTimeOffset.UtcNow), Author = "system", Text = text };

    public async Task<Order> FailAsync(string orderId, OwnerContext owner, string reason)
    {
        var row = await orders.GetRowForOwnerAsync(orderId, owner);
        if (row.PaymentStatus == "paid") return OrderService.ToOrder(row);
        // A failed payment gives its stock back; a retry (via initiate) claims it again.
        await inventory.GiveBackAsync(OrderService.StockLinesOf(row));
        var notes = Json.Deserialize<List<OrderNote>>(row.Notes) ?? [];
        notes.Insert(0, SystemNote($"Payment attempt failed: {reason}"));
        row.PaymentStatus = "failed";
        row.Notes = Json.Serialize(notes);
        await db.SaveChangesAsync();
        return OrderService.ToOrder(row);
    }

    /// <summary>
    /// Razorpay's webhook: the safety net for a payment the browser never reported (closed tab, lost connection, a UPI request approved
    /// later), authenticated only by its signature over the untouched raw body. Always answers ok, so an attacker learns nothing about what a
    /// valid signature looks like, and is idempotent.
    ///
    /// Only a captured payment changes anything. Razorpay sends payment.failed for every failed attempt, also while its window is still open
    /// and the shopper tries another method, so a failure here must not give the stock back (the browser reports a real give-up through fail,
    /// and the payment-deadline sweep releases an abandoned order).
    /// </summary>
    public async Task WebhookAsync(string? rawBody, string? signature)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(rawBody) || !razorpay.VerifyWebhookSignature(rawBody, signature))
        {
            Log.Warn("RazorpayWebhook", "Rejected a webhook call with a missing or invalid signature.");
            return;
        }
        var evt = JsonNode.Parse(rawBody);
        var payment = evt?["payload"]?["payment"]?["entity"];
        var providerOrderId = payment?["order_id"]?.GetValue<string>();
        var paymentId = payment?["id"]?.GetValue<string>();
        if (evt?["event"]?.GetValue<string>() != "payment.captured" || providerOrderId is null || paymentId is null) return;
        var order = await db.Orders.FirstOrDefaultAsync(o => o.ProviderOrderId == providerOrderId);
        if (order is null || order.PaymentStatus == "paid" || order.Status != "pending_payment") return;
        OrderNote? shortfall = null;
        if (order.PaymentStatus == "failed")
        {
            // The browser reported a failure, which released the stock hold, before this payment was captured: take it again.
            try
            {
                await inventory.TakeAsync(OrderService.StockLinesOf(order));
            }
            catch (AppError)
            {
                // The money is taken, so the order is confirmed anyway; staff see why it may not be shippable.
                shortfall = SystemNote("Paid after its stock hold was released, and the items could not be reserved again. Check stock before shipping, or cancel and refund.");
            }
        }
        await MarkPaidAsync(order, paymentId, shortfall);
        Log.Info("RazorpayWebhook", $"Webhook confirmed payment for order {order.Id}");
    }
}

public sealed class AdminOrderQuery
{
    public string? Q { get; set; }
    public string? Status { get; set; }
    public string? PaymentMethod { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>Back-office order management, reusing the same order state machine the customer-facing cancel already enforces.</summary>
public sealed class AdminOrderService(EcomDb db, IServiceScopeFactory scopes, InventoryService inventory, RazorpayService razorpay, Cache.CouponRedemptionService coupons, OutboxService outbox, OrderEventsService orderEvents)
{
    private static readonly Dictionary<string, string> StatusLabel = new() { ["packed"] = "Packed", ["shipped"] = "Shipped", ["delivered"] = "Delivered", ["cancelled"] = "Order cancelled" };

    public async Task<(List<AdminOrderRow> Items, int Total, int Page, int PageSize)> ListAsync(AdminOrderQuery query)
    {
        var q = db.Orders.AsNoTracking().AsQueryable();
        if (query.Status is not null) q = q.Where(o => o.Status == query.Status);
        if (query.PaymentMethod is not null) q = q.Where(o => o.PaymentMethod == query.PaymentMethod);
        if (query.From is not null) q = q.Where(o => o.CreatedAt >= ParseDate(query.From));
        if (query.To is not null) q = q.Where(o => o.CreatedAt <= ParseDate($"{query.To}T23:59:59.999Z"));
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var needle = $"%{query.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            q = q.Where(o => EF.Functions.ILike(o.Id, needle) || EF.Functions.ILike(o.ContactName, needle) || EF.Functions.ILike(o.ContactEmail, needle));
        }
        var pageSize = Math.Max(1, query.PageSize);
        var total = await q.CountAsync();
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Min(Math.Max(1, query.Page), pages);
        var rows = await q.OrderByDescending(o => o.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var items = rows.Select(r => new AdminOrderRow
        {
            Id = r.Id,
            CreatedAt = Ids.Iso(r.CreatedAt),
            CustomerName = r.ContactName,
            ItemCount = Json.Deserialize<List<CartLine>>(r.Lines)!.Sum(l => l.Quantity),
            Total = Json.Deserialize<CartTotals>(r.Totals)!.Total,
            Status = r.Status,
            PaymentStatus = r.PaymentStatus,
            PaymentMethod = r.PaymentMethod,
        }).ToList();
        return (items, total, page, pageSize);
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : throw new ArgumentException("Invalid date");

    private static AdminOrderDetail Detail(OrderRow row)
    {
        var order = OrderService.ToOrder(row);
        return new AdminOrderDetail
        {
            Id = order.Id, Status = order.Status, PaymentStatus = order.PaymentStatus, PaymentMethod = order.PaymentMethod, Lines = order.Lines, Totals = order.Totals,
            CouponCode = order.CouponCode, ShippingMethod = order.ShippingMethod, Contact = order.Contact, Address = order.Address, Timeline = order.Timeline,
            CreatedAt = order.CreatedAt, UserId = order.UserId,
            Notes = Json.Deserialize<List<OrderNote>>(row.Notes) ?? [],
            AllowedNext = [.. Orders.Transitions[order.Status]],
        };
    }

    public async Task<AdminOrderDetail> GetAsync(string id)
    {
        var row = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id) ?? throw new AppError(ApiErrorCode.NotFound, "Order not found");
        return Detail(row);
    }

    public async Task<AdminOrderDetail> AdvanceAsync(string id, string status, string actor)
    {
        var row = await db.Orders.FirstOrDefaultAsync(o => o.Id == id) ?? throw new AppError(ApiErrorCode.NotFound, "Order not found");
        var current = row.Status;
        if (!Orders.CanTransition(current, status)) throw new AppError(ApiErrorCode.Validation, $"An order that is {current.Replace('_', ' ')} cannot move to {status.Replace('_', ' ')}.");

        if (status == "cancelled")
        {
            await inventory.GiveBackAsync(OrderService.StockLinesOf(row));
            if (!string.IsNullOrEmpty(row.CouponCode)) await coupons.ReleaseAsync(row.CouponCode);
            // A real refund, automatically, when this was a captured online payment and Razorpay keys are configured; otherwise (COD, or no
            // keys yet) the order is left refund_pending for a manual refund.
            var paymentStatus = row.PaymentStatus;
            if (row.PaymentStatus == "paid")
            {
                paymentStatus = "refund_pending";
                if (razorpay.Enabled && !string.IsNullOrEmpty(row.ProviderPaymentId))
                    _ = Task.Run(() => AttemptRefundAsync(row.Id, row.ProviderPaymentId, Json.Deserialize<CartTotals>(row.Totals)!.Total.Amount));
            }
            var timeline = Json.Deserialize<List<TimelineEntry>>(row.Timeline)!.Where(t => t.Status is "placed" or "paid" or "confirmed").ToList();
            timeline.Add(new TimelineEntry { Status = "cancelled", Label = $"{StatusLabel[status]} (by staff)", At = Derive.Iso(DateTimeOffset.UtcNow) });
            await using var tx = await db.Database.BeginTransactionAsync();
            row.Status = status;
            row.PaymentStatus = paymentStatus;
            row.Timeline = Json.Serialize(timeline);
            outbox.Write("order.cancelled", new { orderId = id, email = row.ContactEmail, name = row.ContactName, byStaff = true });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        else
        {
            var timeline = Json.Deserialize<List<TimelineEntry>>(row.Timeline)!;
            timeline.Add(new TimelineEntry { Status = status, Label = StatusLabel.GetValueOrDefault(status, status), At = Derive.Iso(DateTimeOffset.UtcNow) });
            row.Status = status;
            row.Timeline = Json.Serialize(timeline);
            await db.SaveChangesAsync();
        }
        await AddNoteInternalAsync(row, actor, $"Status changed to {status.Replace('_', ' ')}.");
        await orderEvents.PublishAsync(OrderService.ToOrder(row));
        return Detail(row);
    }

    /// <summary>
    /// Issues the refund and records the outcome as a note; never fails the caller if Razorpay is briefly unreachable. Runs after the request
    /// has gone, so it works in a scope of its own.
    /// </summary>
    private async Task AttemptRefundAsync(string orderId, string providerPaymentId, long amount)
    {
        using var scope = scopes.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<EcomDb>();
        var outbox2 = scope.ServiceProvider.GetRequiredService<OutboxService>();
        async Task NoteAsync(string text)
        {
            var row = await db2.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (row is null) return;
            var notes = Json.Deserialize<List<OrderNote>>(row.Notes) ?? [];
            notes.Insert(0, new OrderNote { Id = $"note_{Ids.Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}", At = Derive.Iso(DateTimeOffset.UtcNow), Author = "system", Text = text });
            row.Notes = Json.Serialize(notes);
            await db2.SaveChangesAsync();
        }
        try
        {
            await razorpay.RefundAsync(providerPaymentId, amount);
            await NoteAsync("Refund issued via Razorpay.");
            var row = await db2.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            if (row is not null) await outbox2.WriteStandaloneAsync("payment.refunded", new { orderId, email = row.ContactEmail, name = row.ContactName, amount = new Money(amount) });
        }
        catch (Exception error)
        {
            try
            {
                await NoteAsync($"Automatic refund failed ({error.Message}); refund it manually in the Razorpay dashboard.");
            }
            catch (Exception)
            {
                // nothing more to do
            }
        }
    }

    public async Task<AdminOrderDetail> AddNoteAsync(string id, string actor, string text)
    {
        var row = await db.Orders.FirstOrDefaultAsync(o => o.Id == id) ?? throw new AppError(ApiErrorCode.NotFound, "Order not found");
        await AddNoteInternalAsync(row, actor, text);
        return Detail(row);
    }

    private async Task AddNoteInternalAsync(OrderRow row, string actor, string text)
    {
        var notes = Json.Deserialize<List<OrderNote>>(row.Notes) ?? [];
        notes.Insert(0, new OrderNote { Id = $"note_{Ids.Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}", At = Derive.Iso(DateTimeOffset.UtcNow), Author = actor, Text = text });
        row.Notes = Json.Serialize(notes);
        await db.SaveChangesAsync();
    }
}
