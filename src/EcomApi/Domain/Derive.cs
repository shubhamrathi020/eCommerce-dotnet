using System.Globalization;

namespace EcomApi.Domain;

public static class Stock
{
    public const string InStock = "in_stock";
    public const string LowStock = "low_stock";
    public const string OutOfStock = "out_of_stock";
    public const string BackOrder = "backorder";
}

/// <summary>Values derived from products: availability, discounts, summaries, ordering, delivery rules.</summary>
public static class Derive
{
    /// <summary>Stock at or below this many units (across variants) is shown as "Only N left".</summary>
    public const int LowStockThreshold = 5;

    public static long TotalStock(Product p) => p.Variants.Sum(v => v.Stock);

    public static (string Status, long? Left) StockStatusOf(Product p)
    {
        var stock = TotalStock(p);
        if (stock == 0) return (p.Variants.Any(v => v.Backorder is not null) ? Stock.BackOrder : Stock.OutOfStock, null);
        if (stock <= LowStockThreshold) return (Stock.LowStock, stock);
        return (Stock.InStock, null);
    }

    /// <summary>The variant with the lowest price (the first one wins a tie).</summary>
    public static Variant CheapestVariant(Product p)
    {
        var best = p.Variants[0];
        foreach (var v in p.Variants)
            if (v.Price.Amount < best.Price.Amount) best = v;
        return best;
    }

    private static int VariantDiscount(Variant v)
    {
        if (v.Mrp is null || v.Mrp.Amount <= v.Price.Amount) return 0;
        return (int)Money.JsRound((double)(v.Mrp.Amount - v.Price.Amount) / v.Mrp.Amount * 100);
    }

    /// <summary>The biggest percentage discount across a product's variants.</summary>
    public static int BestDiscount(Product p)
    {
        var best = 0;
        foreach (var v in p.Variants) best = Math.Max(best, VariantDiscount(v));
        return best;
    }

    /// <summary>Turns a product into the lightweight card shape.</summary>
    public static ProductSummary ToSummary(Product p)
    {
        var cheapest = CheapestVariant(p);
        var min = p.Variants.Min(v => v.Price.Amount);
        var max = p.Variants.Max(v => v.Price.Amount);
        var (status, left) = StockStatusOf(p);
        var buyable = p.Variants.Where(v => v.Stock > 0 || v.Backorder is not null).ToList();
        var s = new ProductSummary
        {
            Id = p.Id,
            Slug = p.Slug,
            Title = p.Title,
            BrandName = p.BrandName,
            CategoryName = p.CategoryPath[^1].Name,
            PriceMin = Money.Inr(min),
            PriceMax = Money.Inr(max),
            Rating = new SummaryRate { Average = p.Rating.Average, Count = p.Rating.Count },
            StockStatus = status,
            VariantCount = p.Variants.Count,
            StockLeft = left,
        };
        if (p.Images.Count > 0) s.Image = p.Images[0];
        if (p.Images.Count > 1) s.HoverImage = p.Images[1];
        if (VariantDiscount(cheapest) >= 1 && cheapest.Mrp is not null) s.MrpMin = cheapest.Mrp.Copy();
        if (p.Variants.Count == 1 && buyable.Count == 1) s.QuickAddVariantId = buyable[0].Id;
        return s;
    }

    /// <summary>Orders products for a listing. Out-of-stock products rank last; ties break on id so the order is stable.</summary>
    public static List<Product> SortProducts(IEnumerable<Product> products, string key, IReadOnlyDictionary<string, double>? scores = null)
    {
        double Cmp(Product a, Product b)
        {
            switch (key)
            {
                case "featured": return b.Popularity - a.Popularity;
                case "price-asc": return CheapestVariant(a).Price.Amount - CheapestVariant(b).Price.Amount;
                case "price-desc": return CheapestVariant(b).Price.Amount - CheapestVariant(a).Price.Amount;
                case "newest": return string.CompareOrdinal(b.CreatedAt, a.CreatedAt);
                case "rating":
                    var d = b.Rating.Average - a.Rating.Average;
                    return d != 0 ? d : b.Rating.Count - a.Rating.Count;
                case "discount": return BestDiscount(b) - BestDiscount(a);
                default:
                    var sa = scores is not null && scores.TryGetValue(a.Id, out var x) ? x : 0;
                    var sb = scores is not null && scores.TryGetValue(b.Id, out var y) ? y : 0;
                    var ds = sb - sa;
                    return ds != 0 ? ds : b.Popularity - a.Popularity;
            }
        }
        // OrderBy is a stable sort; the comparison puts out-of-stock products last, then applies the key, then the id.
        var list = products.ToList();
        var comparer = Comparer<Product>.Create((a, b) =>
        {
            var aOut = TotalStock(a) == 0 ? 1 : 0;
            var bOut = TotalStock(b) == 0 ? 1 : 0;
            if (aOut != bOut) return aOut.CompareTo(bOut);
            var c = Cmp(a, b);
            if (c != 0) return c < 0 ? -1 : 1;
            return string.CompareOrdinal(a.Id, b.Id);
        });
        return list.Order(comparer).ToList();
    }

    /// <summary>The deterministic delivery rules by pin-code prefix.</summary>
    public static Serviceability ComputeServiceability(string pincode, DateTimeOffset now)
    {
        var first = pincode[0] - '0';
        if (first == 9) return new Serviceability { Serviceable = false, Pincode = pincode };
        var days = 2 + first % 4;
        return new Serviceability { Serviceable = true, Pincode = pincode, EstimatedDays = days, EstimatedDate = Iso(now.AddDays(days)), CodAvailable = first % 2 == 0 };
    }

    /// <summary>Formats a time the way JavaScript's Date#toISOString does.</summary>
    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Iso(DateTime t) => (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
