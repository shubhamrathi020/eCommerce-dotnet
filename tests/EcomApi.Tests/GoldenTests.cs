using System.Text.Json;
using System.Text.Json.Nodes;
using EcomApi.Domain;
using EcomApi.Http;

namespace EcomApi.Tests;

/// <summary>
/// These tests hold the C# port of the shared rules to the TypeScript original: testdata/golden.json is produced by running
/// @ecom/contracts over hundreds of carts, coupons, amounts and products (the generator lives in eCommerce-go/testdata/gen), and the
/// results here must match it exactly.
/// </summary>
public class GoldenTests
{
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "golden.json")))!;
    private static readonly List<Product> Products = JsonSerializer.Deserialize<List<Product>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "products.json")), Json.Web)!;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(Golden["now"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>Structural comparison: key order and number formatting do not matter.</summary>
    private static void SameJson(string name, object? actual, JsonNode? expected)
    {
        var got = JsonNode.Parse(JsonSerializer.Serialize(actual, actual?.GetType() ?? typeof(object), Json.Web));
        Assert.True(JsonNode.DeepEquals(got, expected), $"{name} differs\n got: {got?.ToJsonString()}\nwant: {expected?.ToJsonString()}");
    }

    [Fact]
    public void PriceCartMatchesTypeScript()
    {
        var index = 0;
        foreach (var c in Golden["cartCases"]!.AsArray())
        {
            var stored = JsonSerializer.Deserialize<StoredCart>(c!["stored"]!.ToJsonString(), Json.Web)!;
            var overrides = c["overrides"]?.AsObject();
            var patched = Products.Select(p =>
            {
                var copy = p.Shallow();
                copy.Variants = p.Variants.Select(v =>
                {
                    var o = overrides?[v.Id];
                    if (o is null) return v;
                    return new Variant
                    {
                        Id = v.Id, Sku = v.Sku, Options = v.Options, Price = v.Price, Mrp = v.Mrp, Images = v.Images,
                        Stock = o["stock"] is { } stock ? stock.GetValue<long>() : v.Stock,
                        Backorder = o["backorder"] is JsonObject b ? new Backorder { ExpectedDate = b["expectedDate"]?.GetValue<string>() } : v.Backorder,
                    };
                }).ToList();
                return copy;
            }).ToList();
            var got = Pricing.PriceCart(stored, patched, Now);
            SameJson($"cart #{index}", got.Cart, c["expected"]!["cart"]);
            SameJson($"stored #{index}", got.Stored, c["expected"]!["stored"]);
            index++;
        }
        Assert.True(index >= 400);
    }

    [Fact]
    public void CouponsMatchTypeScript()
    {
        foreach (var c in Golden["coupons"]!.AsArray())
        {
            var got = Pricing.EvaluateCoupon(c!["code"]!.GetValue<string>(), c["subtotal"]!.GetValue<long>(), Now);
            var want = c["expected"]!;
            Assert.Equal(want["ok"]!.GetValue<bool>(), got.Ok);
            Assert.Equal(want["discount"]?.GetValue<long>() ?? 0, got.Discount);
            Assert.Equal(want["message"]?.GetValue<string>() ?? "", got.Message);
        }
    }

    [Fact]
    public void ShippingMatchesTypeScript()
    {
        foreach (var c in Golden["shipping"]!.AsArray())
            Assert.Equal(c!["expected"]!.GetValue<long>(), Pricing.ShippingFee(c["method"]!.GetValue<string>(), c["amt"]!.GetValue<long>(), c["free"]!.GetValue<bool>()));
    }

    [Fact]
    public void MoneyFormattingMatchesTypeScript()
    {
        foreach (var c in Golden["money"]!.AsArray())
            Assert.Equal(c!["expected"]!.GetValue<string>().Replace(' ', ' '), Money.Format(Money.Inr(c["amount"]!.GetValue<long>())));
    }

    [Fact]
    public void ServiceabilityAndCodMatchTypeScript()
    {
        foreach (var c in Golden["pincodes"]!.AsArray())
            SameJson($"serviceability {c!["pincode"]}", Derive.ComputeServiceability(c["pincode"]!.GetValue<string>(), Now), c["expected"]);
        foreach (var c in Golden["cod"]!.AsArray())
        {
            var (enabled, reason) = Pricing.CodEligibility(c!["pincode"]!.GetValue<string>(), c["total"]!.GetValue<long>());
            Assert.Equal(c["expected"]!["enabled"]!.GetValue<bool>(), enabled);
            Assert.Equal(c["expected"]!["reason"]?.GetValue<string>(), reason);
        }
    }

    [Fact]
    public void PasswordsAndPermissionsMatchTypeScript()
    {
        foreach (var c in Golden["passwords"]!.AsArray())
            Assert.Equal(c!["expected"]?.GetValue<string>(), Roles.PasswordProblem(c["password"]!.GetValue<string>()));
        foreach (var c in Golden["permissions"]!.AsArray())
            Assert.Equal(c!["expected"]!.AsArray().Select(x => x!.GetValue<string>()), Roles.PermissionsFor(c["roles"]!.AsArray().Select(x => x!.GetValue<string>())));
    }

    [Fact]
    public void CatalogDerivationsMatchTypeScript()
    {
        var byId = Products.ToDictionary(p => p.Id);
        foreach (var c in Golden["summaries"]!.AsArray()) SameJson($"summary {c!["id"]}", Derive.ToSummary(byId[c["id"]!.GetValue<string>()]), c["expected"]);
        foreach (var c in Golden["stock"]!.AsArray())
        {
            var product = byId[c!["id"]!.GetValue<string>()];
            var (status, left) = Derive.StockStatusOf(product);
            Assert.Equal(c["expected"]!["status"]!.GetValue<string>(), status);
            Assert.Equal(c["expected"]!["left"]?.GetValue<long>(), left);
            Assert.Equal(c["discount"]!.GetValue<int>(), Derive.BestDiscount(product));
        }
        foreach (var c in Golden["sorts"]!.AsArray())
        {
            var ids = Derive.SortProducts(Products, c!["sort"]!.GetValue<string>()).Select(p => p.Id).ToList();
            Assert.Equal(c["expected"]!.AsArray().Select(x => x!.GetValue<string>()), ids);
        }
    }
}
