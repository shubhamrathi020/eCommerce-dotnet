using System.Globalization;
using System.Text.RegularExpressions;

namespace EcomApi.Domain;

public sealed class CartLine
{
    public string ProductId { get; set; } = "";
    public string VariantId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string BrandName { get; set; } = "";
    public ImageRef Image { get; set; } = new();
    public Dictionary<string, string> Options { get; set; } = new();
    public Money UnitPrice { get; set; } = new();
    public Money? Mrp { get; set; }
    public long Quantity { get; set; }
    public long MaxQuantity { get; set; }
    public Money LineTotal { get; set; } = new();
    public Money TaxIncluded { get; set; } = new();
    public string? Issue { get; set; }
    public Backorder? Backorder { get; set; }
    public Money? PreviousUnitPrice { get; set; }
}

public sealed class AppliedCoupon
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public Money Discount { get; set; } = new();
    public bool FreeShipping { get; set; }
}

public sealed class CartTotals
{
    public long ItemCount { get; set; }
    public Money Subtotal { get; set; } = new();
    public Money MrpSavings { get; set; } = new();
    public Money CouponDiscount { get; set; } = new();
    public Money Shipping { get; set; } = new();
    public Money TaxIncluded { get; set; } = new();
    public Money Total { get; set; } = new();
    public Money? AmountToFreeShipping { get; set; }
}

public sealed class Cart
{
    public List<CartLine> Lines { get; set; } = new();
    public AppliedCoupon? Coupon { get; set; }
    public string ShippingMethod { get; set; } = "standard";
    public CartTotals Totals { get; set; } = new();
    public List<string> Notices { get; set; } = new();
    public bool Blocked { get; set; }
}

public sealed class ShippingOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public Money Price { get; set; } = new();
    public int EstimatedDays { get; set; }
    public string EstimatedDate { get; set; } = "";
}

public sealed class PaymentOption
{
    public string Method { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Reason { get; set; }
}

/// <summary>One persisted cart item: the facts a cart needs before pricing.</summary>
public sealed class StoredItem
{
    public string VariantId { get; set; } = "";
    public long Quantity { get; set; }
    public long SeenPrice { get; set; }
}

/// <summary>The persisted shape of a cart.</summary>
public sealed class StoredCart
{
    public List<StoredItem> Items { get; set; } = new();
    public string? CouponCode { get; set; }
    public string ShippingMethod { get; set; } = "standard";

    public static StoredCart Empty() => new();
}

public sealed record Coupon(string Code, string Description, string Kind, long Value = 0, long MinSubtotal = 0, long MaxDiscount = 0, string? ExpiresAt = null);

public sealed record CouponResult(bool Ok, Coupon? Coupon, long Discount, string Message);

public sealed class PricedCart
{
    public Cart Cart { get; set; } = new();
    public StoredCart Stored { get; set; } = new();
}

/// <summary>All money logic lives here, never in the client. Pure: the same answer as the TypeScript original.</summary>
public static class Pricing
{
    public const int MaxLineQuantity = 10;
    public const long FreeShippingThreshold = 49_900;
    public const long StandardShipping = 4_900;
    public const long ExpressShipping = 9_900;
    public const long CodMaxTotal = 500_000;

    public static readonly IReadOnlyList<Coupon> Coupons = new[]
    {
        new Coupon("WELCOME10", "10% off up to ₹500", "percent", Value: 10, MinSubtotal: 99_900, MaxDiscount: 50_000),
        new Coupon("FLAT100", "₹100 off", "flat", Value: 10_000, MinSubtotal: 69_900),
        new Coupon("FREESHIP", "Free shipping", "free_shipping"),
        new Coupon("EXPIRED50", "50% off", "percent", Value: 50, ExpiresAt: "2025-12-31T23:59:59Z"),
    };

    /// <summary>GST rates by top-level category id; prices already include GST.</summary>
    private static readonly Dictionary<string, double> GstByRoot = new() { ["cat-grocery"] = 0.05, ["cat-books"] = 0, ["cat-fashion"] = 0.12 };

    private const double DefaultGst = 0.18;

    private static double GstRateFor(Product p) => GstByRoot.TryGetValue(p.CategoryPath[0].Id, out var rate) ? rate : DefaultGst;

    public static CouponResult EvaluateCoupon(string code, long subtotal, DateTimeOffset now)
    {
        var want = code.Trim().ToUpperInvariant();
        var coupon = Coupons.FirstOrDefault(c => c.Code == want);
        if (coupon is null) return new CouponResult(false, null, 0, "This coupon code is not valid.");
        if (coupon.ExpiresAt is not null && DateTimeOffset.TryParse(coupon.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp) && exp < now)
            return new CouponResult(false, null, 0, "This coupon has expired.");
        if (subtotal < coupon.MinSubtotal)
            return new CouponResult(false, null, 0, $"Add {Money.Format(Money.Inr(coupon.MinSubtotal - subtotal))} more to use this coupon.");
        long discount = 0;
        switch (coupon.Kind)
        {
            case "percent":
                discount = subtotal * coupon.Value / 100;
                if (coupon.MaxDiscount > 0 && discount > coupon.MaxDiscount) discount = coupon.MaxDiscount;
                break;
            case "flat":
                discount = coupon.Value;
                if (subtotal < discount) discount = subtotal;
                break;
        }
        return new CouponResult(true, coupon, discount, "");
    }

    /// <summary>The delivery charge for a method after discounts.</summary>
    public static long ShippingFee(string method, long amountAfterDiscount, bool freeShippingCoupon)
    {
        if (amountAfterDiscount <= 0 || freeShippingCoupon) return 0;
        if (method == "express") return ExpressShipping;
        return amountAfterDiscount >= FreeShippingThreshold ? 0 : StandardShipping;
    }

    /// <summary>Recomputes a full cart from the catalog.</summary>
    public static PricedCart PriceCart(StoredCart stored, IReadOnlyList<Product> products, DateTimeOffset now)
    {
        var notices = new List<string>();
        var lines = new List<CartLine>();
        var keptItems = new List<StoredItem>();

        (Product, Variant)? VariantOf(string variantId)
        {
            foreach (var p in products)
                foreach (var v in p.Variants)
                    if (v.Id == variantId) return (p, v);
            return null;
        }

        foreach (var item in stored.Items)
        {
            if (VariantOf(item.VariantId) is not var (product, variant))
            {
                notices.Add("An item in your cart is no longer available and was removed.");
                continue;
            }
            long max = MaxLineQuantity;
            if (variant.Backorder is null && variant.Stock < max) max = variant.Stock;
            var quantity = item.Quantity;
            var limit = Math.Max(max, 1);
            if (quantity > limit) quantity = limit;
            string? issue = null;
            Money? previous = null;

            if (variant.Stock == 0 && variant.Backorder is null)
            {
                issue = "out_of_stock";
                quantity = item.Quantity;
                notices.Add($"{product.Title} is out of stock. Remove it to continue.");
            }
            else if (quantity < item.Quantity)
            {
                issue = "quantity_reduced";
                notices.Add($"Only {variant.Stock} of {product.Title} available. Quantity updated.");
            }
            if (issue != "out_of_stock" && item.SeenPrice != variant.Price.Amount)
            {
                issue ??= "price_changed";
                previous = Money.Inr(item.SeenPrice);
                notices.Add($"Price of {product.Title} updated from {Money.Format(Money.Inr(item.SeenPrice))} to {Money.Format(variant.Price)}.");
            }

            var lineTotal = variant.Price.Amount * quantity;
            var rate = GstRateFor(product);
            var image = variant.Images is { Count: > 0 } ? variant.Images[0] : product.Images.Count > 0 ? product.Images[0] : new ImageRef();
            var line = new CartLine
            {
                ProductId = product.Id,
                VariantId = variant.Id,
                Slug = product.Slug,
                Title = product.Title,
                BrandName = product.BrandName,
                Image = image,
                Options = variant.Options ?? new Dictionary<string, string>(),
                UnitPrice = variant.Price.Copy(),
                Quantity = quantity,
                MaxQuantity = max,
                LineTotal = Money.Inr(lineTotal),
                TaxIncluded = Money.Inr(lineTotal - lineTotal / (1 + rate)),
            };
            if (variant.Mrp is not null && variant.Mrp.Amount > variant.Price.Amount) line.Mrp = variant.Mrp.Copy();
            if (variant.Backorder is not null && quantity > variant.Stock) line.Backorder = new Backorder { ExpectedDate = variant.Backorder.ExpectedDate };
            if (issue is not null) line.Issue = issue;
            if (previous is not null) line.PreviousUnitPrice = previous;
            lines.Add(line);
            keptItems.Add(new StoredItem { VariantId = item.VariantId, Quantity = quantity, SeenPrice = item.SeenPrice });
        }

        long subtotal = 0, mrpSavings = 0, itemCount = 0;
        double tax = 0;
        var blocked = false;
        foreach (var l in lines)
        {
            if (l.Issue == "out_of_stock")
            {
                blocked = true;
                continue;
            }
            subtotal += l.LineTotal.Amount;
            if (l.Mrp is not null) mrpSavings += (l.Mrp.Amount - l.UnitPrice.Amount) * l.Quantity;
            tax += l.TaxIncluded.Amount;
            itemCount += l.Quantity;
        }

        var couponCode = stored.CouponCode;
        AppliedCoupon? coupon = null;
        long discount = 0;
        if (!string.IsNullOrEmpty(couponCode))
        {
            var result = EvaluateCoupon(couponCode, subtotal, now);
            if (result.Ok)
            {
                discount = result.Discount;
                coupon = new AppliedCoupon { Code = result.Coupon!.Code, Description = result.Coupon.Description, Discount = Money.Inr(discount), FreeShipping = result.Coupon.Kind == "free_shipping" };
            }
            else
            {
                notices.Add($"Coupon {couponCode} was removed. {result.Message}");
                couponCode = null;
            }
        }

        var afterDiscount = subtotal - discount;
        var freeShip = coupon is { FreeShipping: true };
        var shipping = ShippingFee(stored.ShippingMethod, afterDiscount, freeShip);
        var total = afterDiscount + shipping;
        long toFree = 0;
        if (stored.ShippingMethod == "standard" && !freeShip && afterDiscount > 0 && afterDiscount < FreeShippingThreshold) toFree = FreeShippingThreshold - afterDiscount;

        var taxShare = subtotal > 0 ? tax * afterDiscount / subtotal : 0;
        var totals = new CartTotals
        {
            ItemCount = itemCount,
            Subtotal = Money.Inr(subtotal),
            MrpSavings = Money.Inr(mrpSavings),
            CouponDiscount = Money.Inr(discount),
            Shipping = Money.Inr(shipping),
            TaxIncluded = Money.Inr(taxShare),
            Total = Money.Inr(total),
        };
        if (toFree > 0) totals.AmountToFreeShipping = Money.Inr(toFree);

        return new PricedCart
        {
            Cart = new Cart { Lines = lines, ShippingMethod = stored.ShippingMethod, Totals = totals, Notices = notices.Distinct().ToList(), Blocked = blocked, Coupon = coupon },
            Stored = new StoredCart { Items = keptItems, ShippingMethod = stored.ShippingMethod, CouponCode = couponCode },
        };
    }

    private static readonly Regex PincodeRe = new("^[1-9][0-9]{5}$", RegexOptions.Compiled);

    /// <summary>Why a pin code cannot be delivered to: "invalid", "not_deliverable" or null (it can).</summary>
    public static string? DeliverabilityProblem(string pincode, Func<string, bool> serviceable)
    {
        if (!PincodeRe.IsMatch(pincode)) return "invalid";
        return serviceable(pincode) ? null : "not_deliverable";
    }

    /// <summary>Whether cash on delivery is offered and, if not, why.</summary>
    public static (bool Enabled, string? Reason) CodEligibility(string pincode, long total)
    {
        if (total > CodMaxTotal) return (false, "Cash on delivery is available for orders up to ₹5,000.");
        if (pincode.StartsWith('7') || pincode.StartsWith('8')) return (false, "Cash on delivery is not available for this pin code.");
        return (true, null);
    }
}
