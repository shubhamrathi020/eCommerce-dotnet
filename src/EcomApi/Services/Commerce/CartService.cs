using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace EcomApi.Services.Commerce;

/// <summary>
/// Server-side cart: one row per owner ("user:&lt;id&gt;" or "guest:&lt;token&gt;"), priced fresh from the real catalog on every read.
/// The client never computes money.
/// </summary>
public sealed class CartService(EcomDb db, MongoService mongo)
{
    private async Task<List<Product>> ProductsAsync() => await mongo.Products.Find(p => p.Status == "published").ToListAsync();

    private async Task<StoredCart> ReadRowAsync(string ownerKey)
    {
        var row = await db.Carts.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerKey == ownerKey);
        if (row is null) return StoredCart.Empty();
        return new StoredCart
        {
            Items = Json.Deserialize<List<StoredItem>>(row.Items) ?? [],
            ShippingMethod = row.ShippingMethod,
            CouponCode = string.IsNullOrEmpty(row.CouponCode) ? null : row.CouponCode,
        };
    }

    private async Task WriteRowAsync(string ownerKey, StoredCart stored, bool resetReminder = false)
    {
        var items = Json.Serialize(stored.Items);
        var id = Ids.NewId();
        // An upsert in one statement: two first requests for a brand-new cart (a page firing several calls at once) must not collide.
        if (resetReminder)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO carts (id, "ownerKey", items, "couponCode", "shippingMethod", "updatedAt") VALUES ({id}, {ownerKey}, {items}::jsonb, {stored.CouponCode}, {stored.ShippingMethod}, timezone('UTC', now()))
                ON CONFLICT ("ownerKey") DO UPDATE SET items = EXCLUDED.items, "couponCode" = EXCLUDED."couponCode", "shippingMethod" = EXCLUDED."shippingMethod", "updatedAt" = EXCLUDED."updatedAt", "reminderSentAt" = NULL
                """);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO carts (id, "ownerKey", items, "couponCode", "shippingMethod", "updatedAt") VALUES ({id}, {ownerKey}, {items}::jsonb, {stored.CouponCode}, {stored.ShippingMethod}, timezone('UTC', now()))
                ON CONFLICT ("ownerKey") DO UPDATE SET items = EXCLUDED.items, "couponCode" = EXCLUDED."couponCode", "shippingMethod" = EXCLUDED."shippingMethod", "updatedAt" = EXCLUDED."updatedAt"
                """);
    }

    public async Task<PricedCart> PricedAsync(string ownerKey)
    {
        var products = await ProductsAsync();
        var result = Pricing.PriceCart(await ReadRowAsync(ownerKey), products, DateTimeOffset.UtcNow);
        await WriteRowAsync(ownerKey, result.Stored);
        return result;
    }

    /// <summary>Applies the change, then re-prices (acknowledging current prices).</summary>
    private async Task<PricedCart> MutateAsync(string ownerKey, Func<StoredCart, List<Product>, StoredCart> change)
    {
        var products = await ProductsAsync();
        var stored = await ReadRowAsync(ownerKey);
        var next = change(stored, products);
        var variants = products.SelectMany(p => p.Variants).ToList();
        var acknowledged = new StoredCart
        {
            CouponCode = next.CouponCode,
            ShippingMethod = next.ShippingMethod,
            Items = next.Items.Select(item =>
            {
                var variant = variants.FirstOrDefault(v => v.Id == item.VariantId);
                return variant is not null ? new StoredItem { VariantId = item.VariantId, Quantity = item.Quantity, SeenPrice = variant.Price.Amount } : item;
            }).ToList(),
        };
        var result = Pricing.PriceCart(acknowledged, products, DateTimeOffset.UtcNow);
        // A real content change (add, remove, quantity, coupon, shipping) means the cart is being actively used right now, not sitting
        // abandoned: clear any pending reminder flag so a later re-abandonment can still send one more. A plain re-price on read does not.
        await WriteRowAsync(ownerKey, result.Stored, true);
        return result;
    }

    public async Task<Cart> GetAsync(string ownerKey) => (await PricedAsync(ownerKey)).Cart;

    public async Task<Cart> AddAsync(string ownerKey, string variantId, long quantity) =>
        (await MutateAsync(ownerKey, (stored, products) =>
        {
            var variant = products.SelectMany(p => p.Variants).FirstOrDefault(v => v.Id == variantId) ?? throw new AppError(ApiErrorCode.NotFound, "This product is no longer available.");
            if (variant.Stock == 0 && variant.Backorder is null) throw new AppError(ApiErrorCode.Validation, "Sorry, this item is out of stock.");
            var existing = stored.Items.FirstOrDefault(i => i.VariantId == variantId);
            var max = variant.Backorder is not null ? Pricing.MaxLineQuantity : Math.Min(Pricing.MaxLineQuantity, variant.Stock);
            if (existing is not null && existing.Quantity >= max) throw new AppError(ApiErrorCode.Validation, $"You already have the maximum quantity ({max}) of this item.");
            var items = existing is not null
                ? stored.Items.Select(i => i.VariantId == variantId ? new StoredItem { VariantId = i.VariantId, Quantity = Math.Min(max, i.Quantity + quantity), SeenPrice = i.SeenPrice } : i).ToList()
                : [.. stored.Items, new StoredItem { VariantId = variantId, Quantity = Math.Min(max, Math.Max(1, quantity)), SeenPrice = variant.Price.Amount }];
            return new StoredCart { Items = items, CouponCode = stored.CouponCode, ShippingMethod = stored.ShippingMethod };
        })).Cart;

    public async Task<Cart> SetQuantityAsync(string ownerKey, string variantId, long quantity) =>
        (await MutateAsync(ownerKey, (stored, _) => new StoredCart
        {
            CouponCode = stored.CouponCode,
            ShippingMethod = stored.ShippingMethod,
            Items = quantity <= 0
                ? stored.Items.Where(i => i.VariantId != variantId).ToList()
                : stored.Items.Select(i => i.VariantId == variantId ? new StoredItem { VariantId = i.VariantId, Quantity = Math.Min(quantity, Pricing.MaxLineQuantity), SeenPrice = i.SeenPrice } : i).ToList(),
        })).Cart;

    public Task<Cart> RemoveAsync(string ownerKey, string variantId) => SetQuantityAsync(ownerKey, variantId, 0);

    public async Task<Cart> ApplyCouponAsync(string ownerKey, string code)
    {
        var current = await PricedAsync(ownerKey);
        var evaluated = Pricing.EvaluateCoupon(code, current.Cart.Totals.Subtotal.Amount, DateTimeOffset.UtcNow);
        if (!evaluated.Ok) throw new AppError(ApiErrorCode.Validation, evaluated.Message, new Dictionary<string, string> { ["code"] = evaluated.Message });
        return (await MutateAsync(ownerKey, (stored, _) => new StoredCart { Items = stored.Items, ShippingMethod = stored.ShippingMethod, CouponCode = evaluated.Coupon!.Code })).Cart;
    }

    public async Task<Cart> RemoveCouponAsync(string ownerKey) =>
        (await MutateAsync(ownerKey, (stored, _) => new StoredCart { Items = stored.Items, ShippingMethod = stored.ShippingMethod })).Cart;

    public async Task<Cart> SetShippingMethodAsync(string ownerKey, string method) =>
        (await MutateAsync(ownerKey, (stored, _) => new StoredCart { Items = stored.Items, CouponCode = stored.CouponCode, ShippingMethod = method })).Cart;

    public async Task<Cart> ClearAsync(string ownerKey) =>
        (await MutateAsync(ownerKey, (stored, _) => new StoredCart { Items = [], ShippingMethod = stored.ShippingMethod })).Cart;

    /// <summary>
    /// Called right after a successful login or register: moves the guest cart's items into the account's cart (quantities add up, capped)
    /// and empties the guest cart. A no-op when there was no guest cart cookie or it was empty.
    /// </summary>
    public async Task MergeGuestIntoUserAsync(string? guestOwnerKey, string userId)
    {
        if (guestOwnerKey is null) return;
        var guest = await ReadRowAsync(guestOwnerKey);
        if (guest.Items.Count == 0) return;
        var userKey = $"user:{userId}";
        var mine = await ReadRowAsync(userKey);
        var items = mine.Items.Select(i => new StoredItem { VariantId = i.VariantId, Quantity = i.Quantity, SeenPrice = i.SeenPrice }).ToList();
        foreach (var g in guest.Items)
        {
            var existing = items.FirstOrDefault(i => i.VariantId == g.VariantId);
            if (existing is not null) existing.Quantity = Math.Min(Pricing.MaxLineQuantity, existing.Quantity + g.Quantity);
            else items.Add(new StoredItem { VariantId = g.VariantId, Quantity = g.Quantity, SeenPrice = g.SeenPrice });
        }
        await WriteRowAsync(userKey, new StoredCart { Items = items, CouponCode = mine.CouponCode, ShippingMethod = mine.ShippingMethod });
        await db.Carts.Where(c => c.OwnerKey == guestOwnerKey).ExecuteDeleteAsync();
    }

    /// <summary>Empties a cart outright (used after an order is placed or paid).</summary>
    public Task ReplaceWithEmptyAsync(string ownerKey, string shippingMethod) => WriteRowAsync(ownerKey, new StoredCart { Items = [], ShippingMethod = shippingMethod });
}
