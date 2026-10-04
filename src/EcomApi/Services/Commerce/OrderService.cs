using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EcomApi.Cache;
using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services.Messaging;
using Microsoft.EntityFrameworkCore;

namespace EcomApi.Services.Commerce;

public sealed class OwnerContext
{
    public string? UserId { get; init; }
    public string? GuestToken { get; init; }
}

public sealed class PlaceOrderBody
{
    [System.ComponentModel.DataAnnotations.MaxLength(100)] public required string IdempotencyKey { get; set; }
    public required ContactBody Contact { get; set; }
    public required OrderAddressBody Address { get; set; }
    [OneOf("razorpay", "cod")] public required string PaymentMethod { get; set; }
}

public sealed class ContactBody
{
    [System.ComponentModel.DataAnnotations.MaxLength(120)] public required string Name { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(200)] public required string Email { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(20)] public required string Phone { get; set; }
}

public sealed class OrderAddressBody
{
    [System.ComponentModel.DataAnnotations.MaxLength(200)] public required string Line1 { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(200)] public string? Line2 { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(100)] public required string City { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(100)] public required string State { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(10)] public required string Pincode { get; set; }
}

/// <summary>Orders: the state machine, idempotent placement, and stock holds.</summary>
public sealed partial class OrderService(EcomDb db, CartService cart, InventoryService inventory, CouponRedemptionService coupons, OutboxService outbox, OrderEventsService events)
{
    private static readonly Regex EmailRe = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);
    private static readonly Regex PhoneRe = new("^[6-9][0-9]{9}$", RegexOptions.Compiled);

    /// <summary>Online-payment orders hold their stock for this long before a sweep cancels them and releases it.</summary>
    public const int PaymentWindowMinutes = 15;

    public static List<StockLine> StockLinesOf(OrderRow order) => Json.Deserialize<List<CartLine>>(order.Lines)!.Select(l => new StockLine(l.VariantId, l.Quantity)).ToList();

    public static Order ToOrder(OrderRow row)
    {
        var address = new Address { Line1 = row.AddressLine1, City = row.AddressCity, State = row.AddressState, Pincode = row.AddressPincode };
        if (!string.IsNullOrEmpty(row.AddressLine2)) address.Line2 = row.AddressLine2;
        return new Order
        {
            Id = row.Id,
            Status = row.Status,
            PaymentStatus = row.PaymentStatus,
            PaymentMethod = row.PaymentMethod,
            Lines = Json.Deserialize<List<CartLine>>(row.Lines)!,
            Totals = Json.Deserialize<CartTotals>(row.Totals)!,
            CouponCode = string.IsNullOrEmpty(row.CouponCode) ? null : row.CouponCode,
            ShippingMethod = row.ShippingMethod,
            Contact = new ContactDetails { Name = row.ContactName, Email = row.ContactEmail, Phone = row.ContactPhone },
            Address = address,
            Timeline = Json.Deserialize<List<TimelineEntry>>(row.Timeline)!,
            CreatedAt = Ids.Iso(row.CreatedAt),
            UserId = string.IsNullOrEmpty(row.UserId) ? null : row.UserId,
        };
    }

    private static void AssertDeliverable(string pincode)
    {
        var problem = Pricing.DeliverabilityProblem(pincode, p => Derive.ComputeServiceability(p, DateTimeOffset.UtcNow).Serviceable);
        if (problem == "invalid") throw new AppError(ApiErrorCode.Validation, "Enter a valid 6-digit pin code", new Dictionary<string, string> { ["pincode"] = "Invalid pin code" });
        if (problem == "not_deliverable") throw new AppError(ApiErrorCode.Validation, "Sorry, we do not deliver to this pin code yet.", new Dictionary<string, string> { ["pincode"] = "Not deliverable" });
    }

    private static string NewOrderId() => $"ORD-{Ids.Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToUpperInvariant()}{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";

    /// <summary>
    /// Cancels unpaid orders whose payment window ran out, releasing their stock hold. Run twice over: lazily (at the top of every
    /// order-touching call, so a request never sees a stale expired order) and actively every 30 seconds by the scheduler.
    /// </summary>
    public async Task SweepExpiredAsync()
    {
        var now = DateTime.UtcNow;
        var expired = await db.Orders.Where(o => o.Status == "pending_payment" && o.PaymentDeadline < now).ToListAsync();
        foreach (var order in expired)
        {
            await inventory.GiveBackAsync(StockLinesOf(order));
            if (!string.IsNullOrEmpty(order.CouponCode)) await coupons.ReleaseAsync(order.CouponCode);
            var timeline = Json.Deserialize<List<TimelineEntry>>(order.Timeline)!;
            timeline.Add(new TimelineEntry { Status = "cancelled", Label = "Cancelled: payment window expired", At = Derive.Iso(now) });
            await using var tx = await db.Database.BeginTransactionAsync();
            order.Status = "cancelled";
            order.PaymentStatus = "failed";
            order.Timeline = Json.Serialize(timeline);
            outbox.Write("order.expired", new { orderId = order.Id, email = order.ContactEmail, name = order.ContactName });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await events.PublishAsync(ToOrder(order));
        }
    }

    private static bool MatchesOwner(OrderRow row, OwnerContext owner)
    {
        if (owner.UserId is not null) return row.UserId == owner.UserId;
        return row.UserId is null && !string.IsNullOrEmpty(owner.GuestToken) && row.GuestToken == owner.GuestToken;
    }

    public async Task<Order> PlaceAsync(string ownerKey, OwnerContext owner, PlaceOrderBody request)
    {
        await SweepExpiredAsync();
        var existing = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null) return ToOrder(existing);

        var fields = new Dictionary<string, string>();
        if (request.Contact.Name.Trim().Length == 0) fields["name"] = "Name is required";
        if (!EmailRe.IsMatch(request.Contact.Email)) fields["email"] = "Enter a valid email address";
        if (!PhoneRe.IsMatch(request.Contact.Phone)) fields["phone"] = "Enter a valid 10-digit mobile number";
        if (request.Address.Line1.Trim().Length == 0) fields["line1"] = "Address is required";
        if (request.Address.City.Trim().Length == 0) fields["city"] = "City is required";
        if (request.Address.State.Trim().Length == 0) fields["state"] = "State is required";
        AppError.AssertNoFieldErrors(fields);
        AssertDeliverable(request.Address.Pincode);

        var priced = await cart.GetAsync(ownerKey);
        if (priced.Lines.Count == 0) throw new AppError(ApiErrorCode.Validation, "Your cart is empty.");
        if (priced.Blocked) throw new AppError(ApiErrorCode.Validation, "Some items in your cart are out of stock. Remove them to continue.");
        var cod = request.PaymentMethod == "cod";
        if (cod)
        {
            var eligibility = Pricing.CodEligibility(request.Address.Pincode, priced.Totals.Total.Amount);
            if (!eligibility.Enabled) throw new AppError(ApiErrorCode.Validation, eligibility.Reason ?? "Cash on delivery is not available.");
        }

        // A capped coupon is claimed the same way stock is: atomically, before the order exists, so concurrent checkouts can never together
        // exceed its redemption cap.
        var couponCode = priced.Coupon?.Code;
        if (couponCode is not null && !await coupons.TryClaimAsync(couponCode))
            throw new AppError(ApiErrorCode.Validation, "This coupon has reached its usage limit. Please remove it to continue.", new Dictionary<string, string> { ["code"] = "Usage limit reached" });
        // Stock is claimed before the order exists, so two shoppers can never both get the last unit.
        var stockLines = priced.Lines.Select(l => new StockLine(l.VariantId, l.Quantity)).ToList();
        try
        {
            await inventory.TakeAsync(stockLines);
        }
        catch (Exception)
        {
            if (couponCode is not null) await coupons.ReleaseAsync(couponCode);
            throw;
        }

        var now = DateTime.UtcNow;
        var id = NewOrderId();
        var timeline = new List<TimelineEntry> { new() { Status = "placed", Label = "Order placed", At = Derive.Iso(now) } };
        if (cod) timeline.Add(new TimelineEntry { Status = "confirmed", Label = "Order confirmed", At = Derive.Iso(now) });
        var row = new OrderRow
        {
            Id = id,
            Status = cod ? "confirmed" : "pending_payment",
            PaymentStatus = cod ? "cod" : "pending",
            PaymentMethod = request.PaymentMethod,
            UserId = owner.UserId,
            GuestToken = owner.UserId is not null ? null : owner.GuestToken,
            Lines = Json.Serialize(priced.Lines),
            Totals = Json.Serialize(priced.Totals),
            CouponCode = priced.Coupon?.Code,
            ShippingMethod = priced.ShippingMethod,
            ContactName = request.Contact.Name.Trim(),
            ContactEmail = request.Contact.Email.Trim(),
            ContactPhone = request.Contact.Phone,
            AddressLine1 = request.Address.Line1.Trim(),
            AddressLine2 = string.IsNullOrEmpty(request.Address.Line2?.Trim()) ? null : request.Address.Line2!.Trim(),
            AddressCity = request.Address.City.Trim(),
            AddressState = request.Address.State.Trim(),
            AddressPincode = request.Address.Pincode,
            Timeline = Json.Serialize(timeline),
            Notes = "[]",
            IdempotencyKey = request.IdempotencyKey,
            PaymentDeadline = cod ? null : now.AddMinutes(PaymentWindowMinutes),
        };

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Orders.Add(row);
            // The order-confirmation email rides the same transaction as the order row itself, so a request that reports success always really
            // did create both.
            outbox.Write("order.placed", new
            {
                orderId = id,
                email = request.Contact.Email.Trim(),
                name = request.Contact.Name.Trim(),
                total = priced.Totals.Total,
                itemCount = priced.Lines.Sum(l => l.Quantity),
                cod,
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception)
        {
            // Unique idempotency-key race: a concurrent request with the same key won first. Give the stock and the coupon claim back, and
            // return that order instead of creating a duplicate.
            db.ChangeTracker.Clear();
            var raced = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == request.IdempotencyKey);
            await inventory.GiveBackAsync(stockLines);
            if (couponCode is not null) await coupons.ReleaseAsync(couponCode);
            if (raced is not null) return ToOrder(raced);
            throw new AppError(ApiErrorCode.Conflict, "Could not place the order. Please try again.");
        }
        if (cod) await cart.ReplaceWithEmptyAsync(ownerKey, priced.ShippingMethod);
        var placed = ToOrder(await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id));
        await events.PublishAsync(placed);
        return placed;
    }

    public async Task<Order> GetAsync(string orderId, OwnerContext owner)
    {
        await SweepExpiredAsync();
        var row = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
        if (row is null || !MatchesOwner(row, owner)) throw new AppError(ApiErrorCode.NotFound, "Order not found");
        return ToOrder(row);
    }

    public async Task<List<Order>> ListAsync(OwnerContext owner)
    {
        await SweepExpiredAsync();
        var token = owner.GuestToken ?? "\0never";
        var query = db.Orders.AsNoTracking().AsQueryable();
        query = owner.UserId is not null ? query.Where(o => o.UserId == owner.UserId) : query.Where(o => o.UserId == null && o.GuestToken == token);
        return (await query.OrderByDescending(o => o.CreatedAt).ToListAsync()).Select(ToOrder).ToList();
    }

    /// <summary>For the payment module: the raw row, ownership-checked the same way as get and cancel.</summary>
    public async Task<OrderRow> GetRowForOwnerAsync(string orderId, OwnerContext owner)
    {
        var row = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (row is null || !MatchesOwner(row, owner)) throw new AppError(ApiErrorCode.NotFound, "Order not found");
        return row;
    }

    public async Task<Order> CancelAsync(string orderId, OwnerContext owner)
    {
        await SweepExpiredAsync();
        var row = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (row is null || !MatchesOwner(row, owner)) throw new AppError(ApiErrorCode.NotFound, "Order not found");
        if (row.Status == "cancelled") return ToOrder(row);
        if (row.Status is "shipped" or "delivered") throw new AppError(ApiErrorCode.Validation, "This order has already shipped and can no longer be cancelled.");
        await inventory.GiveBackAsync(StockLinesOf(row));
        if (!string.IsNullOrEmpty(row.CouponCode)) await coupons.ReleaseAsync(row.CouponCode);
        var timeline = Json.Deserialize<List<TimelineEntry>>(row.Timeline)!.Where(t => t.Status is "placed" or "paid" or "confirmed").ToList();
        timeline.Add(new TimelineEntry { Status = "cancelled", Label = "Order cancelled", At = Derive.Iso(DateTimeOffset.UtcNow) });
        await using var tx = await db.Database.BeginTransactionAsync();
        row.Status = "cancelled";
        row.PaymentStatus = row.PaymentStatus == "paid" ? "refund_pending" : row.PaymentStatus;
        row.Timeline = Json.Serialize(timeline);
        outbox.Write("order.cancelled", new { orderId, email = row.ContactEmail, name = row.ContactName });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        var order = ToOrder(row);
        await events.PublishAsync(order);
        return order;
    }
}
