using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services.Catalog;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace EcomApi.Controllers;

[Route("catalog")]
public sealed class CatalogController(CatalogService catalog) : ApiControllerBase
{
    private static readonly string[] SortKeys = ["relevance", "featured", "price-asc", "price-desc", "newest", "rating", "discount"];
    private static readonly string[] ReviewSorts = ["recent", "helpful", "high", "low"];

    /// <summary>
    /// Parses filters as a JSON object of string arrays (the shape ListingQuery.Filters needs); anything else (missing, malformed, wrong
    /// types) is treated as "no filters" rather than a 500.
    /// </summary>
    private static Dictionary<string, List<string>> ParseFilters(string? raw)
    {
        var result = new Dictionary<string, List<string>>();
        if (string.IsNullOrEmpty(raw)) return result;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array) result[p.Name] = p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (JsonException)
        {
            return new Dictionary<string, List<string>>();
        }
        return result;
    }

    /// <summary>JavaScript's Number(v) then truncation, with a fallback for anything that is not a finite number.</summary>
    internal static int Int(string? v, int fallback)
    {
        if (v is null) return fallback;
        var trimmed = v.Trim();
        double n;
        if (trimmed.Length == 0) n = 0;
        else if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || !double.IsFinite(n)) return fallback;
        return n >= int.MaxValue ? int.MaxValue : n <= int.MinValue ? int.MinValue : (int)Math.Truncate(n);
    }

    private IActionResult CachedJson(object body, int maxAge)
    {
        var json = Json.Serialize(body);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..27]}\"";
        Response.Headers.CacheControl = $"public, max-age={maxAge}";
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.ToString() == etag) return new ApiResult(304, null);
        return Ok(body);
    }

    [HttpGet("home")]
    public async Task<IActionResult> Home() => CachedJson(await catalog.HomeAsync(), 60);

    [HttpGet("categories/tree")]
    public async Task<IActionResult> CategoryTree() => CachedJson(await catalog.CategoryTreeAsync(), 300);

    [HttpGet("listing")]
    public async Task<IActionResult> Listing()
    {
        string? Q(string key) => Request.Query.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;
        var query = new ListingQuery
        {
            CategorySlug = string.IsNullOrEmpty(Q("categorySlug")) ? null : Q("categorySlug"),
            BrandSlug = string.IsNullOrEmpty(Q("brandSlug")) ? null : Q("brandSlug"),
            CollectionSlug = string.IsNullOrEmpty(Q("collectionSlug")) ? null : Q("collectionSlug"),
            Q = string.IsNullOrEmpty(Q("q")) ? null : Q("q"),
            Filters = ParseFilters(Q("filters")),
            PriceMin = Q("priceMin") is { } pmin ? Int(pmin, 0) : null,
            PriceMax = Q("priceMax") is { } pmax ? Int(pmax, 0) : null,
            Sort = SortKeys.Contains(Q("sort")) ? Q("sort")! : "relevance",
            Page = Int(Q("page"), 1),
            PageSize = Math.Min(100, Int(Q("pageSize"), 24)),
        };
        return CachedJson(await catalog.ListingAsync(query), 30);
    }

    [HttpGet("products/{slug}")]
    public async Task<IActionResult> Product(string slug) => CachedJson(await catalog.ProductAsync(slug), 120);

    private static List<string> IdsOf(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array) return [];
        return ids.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    [HttpPost("products/by-ids")]
    public async Task<IActionResult> ProductsByIds([FromBody] JsonElement? body) => Created(await catalog.ProductsByIdsAsync(IdsOf(body)));

    [HttpPost("products/summaries")]
    public async Task<IActionResult> SummariesByIds([FromBody] JsonElement? body) => Created(await catalog.SummariesByIdsAsync(IdsOf(body)));

    [HttpGet("products/{id}/related")]
    public async Task<IActionResult> Related(string id) => Ok(await catalog.RelatedAsync(id));

    [HttpGet("products/{id}/bought-together")]
    public async Task<IActionResult> BoughtTogether(string id) => Ok(await catalog.BoughtTogetherAsync(id));

    [HttpGet("products/{id}/reviews")]
    public async Task<IActionResult> Reviews(string id)
    {
        string? Q(string key) => Request.Query.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;
        var sort = ReviewSorts.Contains(Q("sort")) ? Q("sort")! : "recent";
        return Ok(await catalog.ReviewsAsync(id, new ReviewQuery { Sort = sort, Page = Int(Q("page"), 1), PageSize = Math.Min(50, Int(Q("pageSize"), 10)) }));
    }

    [HttpGet("serviceability/{pincode}")]
    public IActionResult Serviceability(string pincode) => Ok(catalog.Serviceability(pincode));
}

[Route("search")]
public sealed class SearchController(SearchService search, CatalogService catalog, MongoService mongo) : ApiControllerBase
{
    private static readonly string[] PopularSearches = ["smartphone", "sneakers", "t-shirt", "laptop", "headphones", "yoga mat", "air fryer", "watch"];
    private const int MaxQueryLength = 100;

    /// <summary>Suggestions beyond the results list. Results themselves come from GET /catalog/listing, so Meilisearch does the real matching.</summary>
    [HttpGet("suggest"), RateBucket("search")]
    public async Task<IActionResult> Suggest()
    {
        // A repeated ?q=a&q=b is an array in Express and fails there; here it is two values, which fails the same way.
        if (Request.Query.TryGetValue("q", out var values) && values.Count > 1) throw new InvalidOperationException("q must be a single value");
        var raw = values.Count == 1 ? values[0] ?? "" : "";
        var q = (raw.Length > MaxQueryLength ? raw[..MaxQueryLength] : raw).Trim();
        if (q.Length == 0) return Ok(new SearchSuggestions { Queries = PopularSearches.Take(5).ToList() });
        // Product suggestions need Meilisearch; category and brand suggestions are plain MongoDB reads. A search outage should not also take
        // those down: it degrades to an empty product list rather than failing the whole dropdown.
        async Task<List<string>> ProductIds()
        {
            try
            {
                return await search.SuggestProductsAsync(q, 4);
            }
            catch (Exception)
            {
                return [];
            }
        }
        var idsTask = ProductIds();
        var categoriesTask = mongo.Categories.Find(new MongoDB.Bson.BsonDocument("name", new MongoDB.Bson.BsonDocument { ["$regex"] = q, ["$options"] = "i" })).Limit(3).ToListAsync();
        var brandsTask = mongo.Brands.Find(new MongoDB.Bson.BsonDocument("name", new MongoDB.Bson.BsonDocument { ["$regex"] = q, ["$options"] = "i" })).Limit(3).ToListAsync();
        await Task.WhenAll(idsTask, categoriesTask, brandsTask);
        var products = await catalog.SummariesByIdsAsync(idsTask.Result);
        var popularMatches = PopularSearches.Where(p => p.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        return Ok(new SearchSuggestions
        {
            Queries = popularMatches.Concat(categoriesTask.Result.Select(c => c.Name)).Concat(brandsTask.Result.Select(b => b.Name)).Distinct().Take(5).ToList(),
            Products = products,
            Categories = categoriesTask.Result.Select(c => new NameSlug { Slug = c.Slug, Name = c.Name }).ToList(),
            Brands = brandsTask.Result.Select(b => new NameSlug { Slug = b.Slug, Name = b.Name }).ToList(),
        });
    }

    [HttpGet("popular")]
    public IActionResult Popular() => Ok(PopularSearches);
}

public sealed class BulkIdsBody
{
    public required List<JsonElement> Ids { get; set; }
}

public sealed class BulkStatusBody
{
    public required List<JsonElement> Ids { get; set; }
    [OneOf("draft", "published", "archived")] public required string Status { get; set; }
}

/// <summary>Back-office product management.</summary>
[Route("admin/products")]
public sealed class AdminCatalogController(AdminCatalogService admin) : ApiControllerBase
{
    private static readonly string[] Statuses = ["draft", "published", "archived"];
    private static readonly string[] Sorts = ["updated", "title", "price", "stock"];

    private static List<string> Strings(List<JsonElement> ids) => ids.Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();

    [HttpGet, RequireAuth("product:read")]
    public async Task<IActionResult> List()
    {
        string? Q(string key) => Request.Query.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;
        return Ok(await admin.ListAsync(new AdminProductQuery
        {
            Q = string.IsNullOrEmpty(Q("q")) ? null : Q("q"),
            Status = Q("status") is { Length: > 0 } s && Statuses.Contains(s) ? s : null,
            Sort = Sorts.Contains(Q("sort")) ? Q("sort")! : "updated",
            Dir = Q("dir") == "asc" ? "asc" : "desc",
            Page = CatalogController.Int(Q("page"), 1),
            PageSize = Math.Min(200, CatalogController.Int(Q("pageSize"), 20)),
        }));
    }

    [HttpGet("categories"), RequireAuth("product:read")]
    public async Task<IActionResult> Categories() => Ok(await admin.CategoriesAsync());

    [HttpGet("{id}"), RequireAuth("product:read")]
    public async Task<IActionResult> Get(string id) => Ok(await admin.GetAsync(id));

    [HttpPost, RequireAuth("product:write")]
    public async Task<IActionResult> Create([FromBody] AdminProductBody body) => Created(await admin.CreateAsync(body.ToInput()));

    [HttpPut("{id}"), RequireAuth("product:write")]
    public async Task<IActionResult> Update(string id, [FromBody] AdminProductBody body) => Ok(await admin.UpdateAsync(id, body.ToInput()));

    [HttpPatch("bulk-status"), RequireAuth("product:write")]
    public async Task<IActionResult> BulkSetStatus([FromBody] BulkStatusBody body) => Ok(new { count = await admin.BulkSetStatusAsync(Strings(body.Ids), body.Status) });

    [HttpPatch("bulk-delete-drafts"), RequireAuth("product:write")]
    public async Task<IActionResult> BulkDeleteDrafts([FromBody] BulkIdsBody body) => Ok(new { count = await admin.BulkDeleteDraftsAsync(Strings(body.Ids)) });
}

public sealed class AdminVariantBody
{
    public string? Id { get; set; }
    public required string Sku { get; set; }
    public required Dictionary<string, JsonElement> Options { get; set; }
    [Range(0, double.MaxValue)] public required double Price { get; set; }
    [Range(0, double.MaxValue)] public double? Mrp { get; set; }
    [Range(0, long.MaxValue)] public required long Stock { get; set; }
}

public sealed class AdminProductBody
{
    public required string Title { get; set; }
    public required string BrandName { get; set; }
    public required string CategoryId { get; set; }
    public required string Description { get; set; }
    public required List<JsonElement> Highlights { get; set; }
    public required List<JsonElement> Tags { get; set; }
    [OneOf("draft", "published", "archived")] public required string Status { get; set; }
    public required List<AdminVariantBody> Variants { get; set; }

    public AdminProductInput ToInput() => new()
    {
        Title = Title,
        BrandName = BrandName,
        CategoryId = CategoryId,
        Description = Description,
        Highlights = Highlights,
        Tags = Tags,
        Status = Status,
        Variants = Variants.Select(v => new AdminVariantInput { Id = v.Id, Sku = v.Sku, Options = v.Options, Price = v.Price, Mrp = v.Mrp, Stock = v.Stock }).ToList(),
    };
}
