using System.ComponentModel.DataAnnotations;
using System.Text;
using EcomApi.Config;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services.Commerce;
using EcomApi.Services.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace EcomApi.Controllers;

public sealed class AddToCartBody
{
    public required string VariantId { get; set; }
    [Range(1, double.MaxValue)] public required long Quantity { get; set; }
}

public sealed class SetQuantityBody
{
    [Range(0, double.MaxValue)] public required long Quantity { get; set; }
}

public sealed class ApplyCouponBody
{
    public required string Code { get; set; }
}

public sealed class SetShippingBody
{
    [OneOf("standard", "express")] public required string Method { get; set; }
}

/// <summary>
/// Server-side cart: works both signed in (bearer token) and as a guest (a long-lived, non-sensitive cookie identifies the cart). Mutations
/// additionally need the CSRF header, since the guest cart cookie is a cookie too.
/// </summary>
[Route("cart"), OptionalAuth]
public sealed class CartController(CartService cart, ApiConfig config) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await cart.GetAsync(CartKey(config)));

    [HttpPost("items"), Csrf]
    public async Task<IActionResult> Add([FromBody] AddToCartBody body) => Created(await cart.AddAsync(CartKey(config), body.VariantId, body.Quantity));

    [HttpPut("items/{variantId}"), Csrf]
    public async Task<IActionResult> SetQuantity(string variantId, [FromBody] SetQuantityBody body) => Ok(await cart.SetQuantityAsync(CartKey(config), variantId, body.Quantity));

    [HttpDelete("items/{variantId}"), Csrf]
    public async Task<IActionResult> Remove(string variantId) => Ok(await cart.RemoveAsync(CartKey(config), variantId));

    [HttpPost("coupon"), Csrf, RateBucket("coupon")]
    public async Task<IActionResult> ApplyCoupon([FromBody] ApplyCouponBody body) => Created(await cart.ApplyCouponAsync(CartKey(config), body.Code));

    [HttpDelete("coupon"), Csrf]
    public async Task<IActionResult> RemoveCoupon() => Ok(await cart.RemoveCouponAsync(CartKey(config)));

    [HttpPut("shipping-method"), Csrf]
    public async Task<IActionResult> SetShippingMethod([FromBody] SetShippingBody body) => Ok(await cart.SetShippingMethodAsync(CartKey(config), body.Method));

    [HttpDelete, Csrf]
    public async Task<IActionResult> Clear() => Ok(await cart.ClearAsync(CartKey(config)));
}

/// <summary>Delivery and payment choices for the current cart, guest or signed in.</summary>
[Route("checkout"), OptionalAuth]
public sealed class CheckoutController(CartService cart, ApiConfig config) : ApiControllerBase
{
    private const long DayMs = 86_400_000;

    private static void AssertDeliverable(string pincode)
    {
        var problem = Pricing.DeliverabilityProblem(pincode, p => Derive.ComputeServiceability(p, DateTimeOffset.UtcNow).Serviceable);
        if (problem == "invalid") throw new AppError(ApiErrorCode.Validation, "Enter a valid 6-digit pin code", new Dictionary<string, string> { ["pincode"] = "Invalid pin code" });
        if (problem == "not_deliverable") throw new AppError(ApiErrorCode.Validation, "Sorry, we do not deliver to this pin code yet.", new Dictionary<string, string> { ["pincode"] = "Not deliverable" });
    }

    [HttpGet("shipping-options/{pincode}")]
    public async Task<IActionResult> ShippingOptions(string pincode)
    {
        AssertDeliverable(pincode);
        var current = await cart.GetAsync(CartKey(config));
        var baseline = Derive.ComputeServiceability(pincode, DateTimeOffset.UtcNow);
        var afterDiscount = current.Totals.Subtotal.Amount - current.Totals.CouponDiscount.Amount;
        var free = current.Coupon?.FreeShipping ?? false;
        var standardDays = baseline.EstimatedDays ?? 3;
        var expressDays = Math.Max(1, standardDays - 2);
        string On(int days) => Derive.Iso(DateTimeOffset.UtcNow.AddMilliseconds(days * (double)DayMs));
        return Ok(new List<ShippingOption>
        {
            new() { Id = "standard", Label = "Standard delivery", Price = Money.Inr(Pricing.ShippingFee("standard", afterDiscount, free)), EstimatedDays = standardDays, EstimatedDate = On(standardDays) },
            new() { Id = "express", Label = "Express delivery", Price = Money.Inr(free ? 0 : Pricing.ExpressShipping), EstimatedDays = expressDays, EstimatedDate = On(expressDays) },
        });
    }

    [HttpGet("payment-options/{pincode}")]
    public async Task<IActionResult> PaymentOptions(string pincode)
    {
        AssertDeliverable(pincode);
        var current = await cart.GetAsync(CartKey(config));
        var cod = Pricing.CodEligibility(pincode, current.Totals.Total.Amount);
        return Ok(new List<PaymentOption>
        {
            new() { Method = "razorpay", Label = "Pay online (UPI, cards, net banking, wallets)", Enabled = true },
            new() { Method = "cod", Label = "Cash on delivery", Enabled = cod.Enabled, Reason = cod.Reason },
        });
    }
}

public sealed class FailPaymentBody
{
    public required string Reason { get; set; }
}

/// <summary>Orders, guest or signed in: the same optional-authentication pattern as the cart.</summary>
[Route("orders"), OptionalAuth]
public sealed class OrderController(OrderService orders, OrderEventsService orderEvents, PaymentService payments, ApiConfig config) : ApiControllerBase
{
    [HttpPost, Csrf, RateBucket("checkout")]
    public async Task<IActionResult> Place([FromBody] PlaceOrderBody body)
    {
        var guestToken = Cookies.EnsureGuestToken(Request, Response, config.Production);
        var user = OptionalUser;
        var ownerKey = Cookies.OwnerKeyFor(user?.Id, guestToken);
        return Created(await orders.PlaceAsync(ownerKey, user is not null ? new OwnerContext { UserId = user.Id } : new OwnerContext { GuestToken = guestToken }, body));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id) => Ok(await orders.GetAsync(id, Owner()));

    /// <summary>
    /// Real-time order tracking: a status change (payment confirmed, packed, shipped, cancelled ...) reaches an open tracking page immediately
    /// over Server-Sent Events. Ownership is checked before the stream opens, so a non-owner gets an ordinary 404 instead of a 200 with an
    /// error event inside the stream. The current status is sent first, so the page has something to show immediately.
    /// </summary>
    [HttpGet("{id}/stream")]
    public async Task Stream(string id, CancellationToken ct)
    {
        var current = await orders.GetAsync(id, Owner());
        // From here on the response is streamed: take the real network stream instead of the buffering one.
        if (HttpContext.Items.TryGetValue(Pipeline.OriginalBodyKey, out var original) && original is Stream stream) Response.Body = stream;
        Response.StatusCode = 200;
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "private, no-cache, no-store, must-revalidate, max-age=0, no-transform";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.Body.FlushAsync(ct);
        await orderEvents.ObserveAsync(id, current, async json =>
        {
            await Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
            await Response.Body.FlushAsync(ct);
        }, ct);
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await orders.ListAsync(Owner()));

    [HttpPost("{id}/cancel"), Csrf]
    public async Task<IActionResult> Cancel(string id) => Ok(await orders.CancelAsync(id, Owner()));

    [HttpPost("{id}/payment/initiate"), Csrf]
    public async Task<IActionResult> InitiatePayment(string id) => Created(await payments.InitiateAsync(id, Owner()));

    [HttpPost("{id}/payment/confirm"), Csrf]
    public async Task<IActionResult> ConfirmPayment(string id, [FromBody] PaymentResultBody body) => Created(await payments.ConfirmAsync(id, Owner(), body));

    [HttpPost("{id}/payment/fail"), Csrf]
    public async Task<IActionResult> FailPayment(string id, [FromBody] FailPaymentBody body) => Ok(await payments.FailAsync(id, Owner(), body.Reason));
}

/// <summary>
/// Razorpay calls this directly; it never goes through a browser, so there is no cookie or bearer session and no CSRF concern: the webhook
/// signature, checked against the untouched raw body, is the only authentication.
/// </summary>
public sealed class WebhookController(PaymentService payments) : ApiControllerBase
{
    [HttpPost("webhooks/razorpay")]
    public async Task<IActionResult> Razorpay()
    {
        HttpContext.Items.TryGetValue(Pipeline.RawBodyKey, out var raw);
        await payments.WebhookAsync(raw as string, Request.Headers["x-razorpay-signature"].ToString() is { Length: > 0 } sig ? sig : null);
        return Ok(new { ok = true });
    }
}

public sealed class AdvanceOrderBody
{
    [OneOf("pending_payment", "confirmed", "packed", "shipped", "delivered", "cancelled")] public required string Status { get; set; }
}

public sealed class AddNoteBody
{
    [MaxLength(2000)] public required string Text { get; set; }
}

/// <summary>Back-office order management against real orders.</summary>
[Route("admin/orders")]
public sealed class AdminOrderController(AdminOrderService admin) : ApiControllerBase
{
    private static readonly string[] Statuses = ["pending_payment", "confirmed", "packed", "shipped", "delivered", "cancelled"];
    private static readonly string[] PaymentMethods = ["razorpay", "cod"];

    [HttpGet, RequireAuth("order:read:any")]
    public async Task<IActionResult> List()
    {
        string? Q(string key) => Request.Query.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;
        var (items, total, page, pageSize) = await admin.ListAsync(new AdminOrderQuery
        {
            Q = string.IsNullOrEmpty(Q("q")) ? null : Q("q"),
            Status = Q("status") is { Length: > 0 } s && Statuses.Contains(s) ? s : null,
            PaymentMethod = Q("paymentMethod") is { Length: > 0 } m && PaymentMethods.Contains(m) ? m : null,
            From = string.IsNullOrEmpty(Q("from")) ? null : Q("from"),
            To = string.IsNullOrEmpty(Q("to")) ? null : Q("to"),
            Page = CatalogController.Int(Q("page"), 1),
            PageSize = Math.Min(200, CatalogController.Int(Q("pageSize"), 20)),
        });
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id}"), RequireAuth("order:read:any")]
    public async Task<IActionResult> Get(string id) => Ok(await admin.GetAsync(id));

    [HttpPatch("{id}/status"), RequireAuth("order:refund")]
    public async Task<IActionResult> Advance(string id, [FromBody] AdvanceOrderBody body) => Ok(await admin.AdvanceAsync(id, body.Status, CurrentUser.Id));

    [HttpPost("{id}/notes"), RequireAuth("order:read:any")]
    public async Task<IActionResult> AddNote(string id, [FromBody] AddNoteBody body) => Created(await admin.AddNoteAsync(id, CurrentUser.Id, body.Text));
}
