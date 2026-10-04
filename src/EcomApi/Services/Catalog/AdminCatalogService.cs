using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EcomApi.Cache;
using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EcomApi.Services.Catalog;

public sealed class Paged<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public sealed class AdminProductRow
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string BrandName { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string Status { get; set; } = "";
    public Money PriceMin { get; set; } = new();
    public long StockTotal { get; set; }
    public int VariantCount { get; set; }
    public ImageRef Image { get; set; } = new();
    public string UpdatedAt { get; set; } = "";
}

public sealed class AdminVariantDetail
{
    public string Id { get; set; } = "";
    public string Sku { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = new();
    public long Price { get; set; }
    public long? Mrp { get; set; }
    public long Stock { get; set; }
}

public sealed class AdminProductDetail
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string BrandName { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Highlights { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string Status { get; set; } = "";
    public List<string> VariantAxes { get; set; } = [];
    public string UpdatedAt { get; set; } = "";
    public List<AdminVariantDetail> Variants { get; set; } = [];
}

public sealed class AdminCategoryOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AdminProductQuery
{
    public string? Q { get; set; }
    public string? Status { get; set; }
    public string Sort { get; set; } = "updated";
    public string Dir { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class AdminVariantInput
{
    public string? Id { get; set; }
    public required string Sku { get; set; }
    public required Dictionary<string, JsonElement> Options { get; set; }
    public required double Price { get; set; }
    public double? Mrp { get; set; }
    public required long Stock { get; set; }
}

public sealed class AdminProductInput
{
    public required string Title { get; set; }
    public required string BrandName { get; set; }
    public required string CategoryId { get; set; }
    public required string Description { get; set; }
    public required List<JsonElement> Highlights { get; set; }
    public required List<JsonElement> Tags { get; set; }
    public required string Status { get; set; }
    public required List<AdminVariantInput> Variants { get; set; }
}

/// <summary>The Meilisearch document for a product: flattened and denormalised so search and facet counting never touch MongoDB per hit.</summary>
public static class SearchDocs
{
    private static string JsString(BsonValue value) => value.BsonType switch
    {
        BsonType.String => value.AsString,
        BsonType.Boolean => value.AsBoolean ? "true" : "false",
        BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.Double => value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
        BsonType.Array => string.Join(",", value.AsBsonArray.Select(v => v.IsBsonNull ? "" : JsString(v))),
        BsonType.Null => "null",
        _ => "[object Object]",
    };

    /// <summary>Every attribute and variant-axis value on this product, one string array per key (e.g. size: ["S","M"]).</summary>
    public static Dictionary<string, List<string>> FacetValuesOf(Product p)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var element in p.Attributes) result[element.Name] = [JsString(element.Value)];
        foreach (var axis in p.VariantAxes)
        {
            var values = p.Variants.Select(v => v.Options.GetValueOrDefault(axis)).Where(v => v is not null).Select(v => v!).Distinct().ToList();
            if (values.Count > 0) result[axis] = values;
        }
        return result;
    }

    public static JsonObject ToSearchDoc(Product p)
    {
        var doc = new JsonObject
        {
            ["id"] = p.Id,
            ["slug"] = p.Slug,
            ["title"] = p.Title,
            ["brandName"] = p.BrandName,
            ["brandSlug"] = Regex.Replace(p.BrandId, "^brand-", ""),
            ["categoryId"] = p.CategoryId,
            ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["priceMin"] = p.Variants.Min(v => v.Price.Amount),
            ["priceMax"] = p.Variants.Max(v => v.Price.Amount),
            ["ratingAverage"] = p.Rating.Average,
            ["bestDiscount"] = Derive.BestDiscount(p),
            ["totalStock"] = Derive.TotalStock(p),
            ["popularity"] = p.Popularity,
            ["createdAtTs"] = DateTimeOffset.Parse(p.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds(),
        };
        foreach (var (key, values) in FacetValuesOf(p)) doc[$"attr_{key}"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return doc;
    }
}

/// <summary>
/// Back-office product management: the same MongoDB store the shop reads, plus keeping Meilisearch in sync on every write (published in,
/// anything else out) with a direct write-through.
/// </summary>
public sealed partial class AdminCatalogService(MongoService mongo, SearchService search, CacheService cache)
{
    private static Money Inr(double amount) => Money.Inr(amount);

    private static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Paragraphs(string text) =>
        string.Concat(Regex.Split(text, @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => $"<p>{EscapeHtml(p)}</p>"));

    private static string PlainText(string html) =>
        Regex.Replace(Regex.Replace(html, @"</p>\s*<p>", "\n\n"), "<[^>]+>", "").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&amp;", "&");

    private static string Slugify(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-"), "^-|-$", "");

    private static string NowIso() => Derive.Iso(DateTimeOffset.UtcNow);

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();

    private static void ValidateInput(AdminProductInput input, HashSet<string> categoryIds, List<Variant> others)
    {
        var fields = new Dictionary<string, string>();
        if (input.Title.Trim().Length == 0) fields["title"] = "Title is required";
        else if (input.Title.Length > 120) fields["title"] = "Title is too long (120 characters at most)";
        if (input.BrandName.Trim().Length == 0) fields["brandName"] = "Brand is required";
        if (!categoryIds.Contains(input.CategoryId)) fields["categoryId"] = "Choose a category";
        if (input.Description.Length > 2000) fields["description"] = "Description is too long (2,000 characters at most)";
        if (input.Variants.Count == 0) fields["variants"] = "Add at least one variant";
        var skus = new HashSet<string>();
        for (var i = 0; i < input.Variants.Count; i++)
        {
            var v = input.Variants[i];
            var sku = v.Sku.Trim();
            if (sku.Length == 0) fields[$"variants.{i}.sku"] = "SKU is required";
            else if (skus.Contains(sku) || others.Any(o => o.Sku == sku && o.Id != v.Id)) fields[$"variants.{i}.sku"] = "SKU must be unique";
            skus.Add(sku);
            if (!(v.Price > 0)) fields[$"variants.{i}.price"] = "Price must be greater than zero";
            if (v.Mrp is not null && v.Mrp < v.Price) fields[$"variants.{i}.mrp"] = "MRP cannot be lower than the price";
            if (v.Stock < 0) fields[$"variants.{i}.stock"] = "Stock must be a whole number, 0 or more";
        }
        AppError.AssertNoFieldErrors(fields);
    }

    private static List<Variant> BuildVariants(string productId, AdminProductInput input, List<Variant> existing) =>
        input.Variants.Select((v, i) =>
        {
            var previous = existing.FirstOrDefault(e => e.Id == v.Id);
            return new Variant
            {
                Id = previous?.Id ?? $"{productId}-v{Ids.Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}{i}",
                Sku = v.Sku.Trim(),
                Options = v.Options.ToDictionary(kv => kv.Key, kv => Str(kv.Value)),
                Price = Inr(v.Price),
                Mrp = v.Mrp is > 0 && v.Mrp > v.Price ? Inr(v.Mrp.Value) : null,
                // There is no stock ledger yet: the admin form's number is the direct source of truth in MongoDB.
                Stock = v.Stock,
                Images = previous?.Images,
            };
        }).ToList();

    private static AdminProductRow ToRow(Product doc) => new()
    {
        Id = doc.Id,
        Slug = doc.Slug,
        Title = doc.Title,
        BrandName = doc.BrandName,
        CategoryName = doc.CategoryPath[^1].Name,
        Status = doc.Status ?? "",
        PriceMin = Inr(doc.Variants.Min(v => v.Price.Amount)),
        StockTotal = doc.Variants.Sum(v => v.Stock),
        VariantCount = doc.Variants.Count,
        Image = doc.Images.Count > 0 ? doc.Images[0] : new ImageRef(),
        UpdatedAt = doc.UpdatedAt ?? "",
    };

    private static AdminProductDetail ToDetail(Product doc) => new()
    {
        Id = doc.Id,
        Slug = doc.Slug,
        Title = doc.Title,
        BrandName = doc.BrandName,
        CategoryId = doc.CategoryId,
        CategoryName = doc.CategoryPath[^1].Name,
        Description = PlainText(doc.Description),
        Highlights = doc.Highlights,
        Tags = doc.Tags,
        Status = doc.Status ?? "",
        VariantAxes = doc.VariantAxes,
        UpdatedAt = doc.UpdatedAt ?? "",
        Variants = doc.Variants.Select(v => new AdminVariantDetail { Id = v.Id, Sku = v.Sku, Options = v.Options, Price = v.Price.Amount, Mrp = v.Mrp?.Amount, Stock = v.Stock }).ToList(),
    };

    private async Task SyncIndexAsync(Product doc)
    {
        if (doc.Status == "published") await search.IndexProductsAsync([SearchDocs.ToSearchDoc(doc)]);
        else
        {
            try
            {
                await search.DeleteProductAsync(doc.Id);
            }
            catch (Exception)
            {
                // The product may never have been indexed.
            }
        }
        // Every cached view of this product, plus the broad home/listing bucket: a status or price change can move this product in or out of
        // listings the cache has no per-entry record of.
        await Task.WhenAll(cache.InvalidateTagAsync($"product:{doc.Id}"), cache.InvalidateTagAsync(CatalogService.ListingsTag));
    }

    public async Task<Paged<AdminProductRow>> ListAsync(AdminProductQuery query)
    {
        var q = query.Q?.Trim().ToLowerInvariant();
        var filter = query.Status is not null ? Builders<Product>.Filter.Eq(p => p.Status, query.Status) : FilterDefinition<Product>.Empty;
        var rows = await mongo.Products.Find(filter).ToListAsync();
        if (!string.IsNullOrEmpty(q))
            rows = rows.Where(d => $"{d.Title} {d.BrandName} {string.Join(" ", d.Variants.Select(v => v.Sku))}".ToLowerInvariant().Contains(q)).ToList();
        Func<Product, Product, int> cmp = query.Sort switch
        {
            "title" => (a, b) => Js.LocaleCompare(a.Title, b.Title),
            "price" => (a, b) => a.Variants.Min(v => v.Price.Amount).CompareTo(b.Variants.Min(v => v.Price.Amount)),
            "stock" => (a, b) => a.Variants.Sum(v => v.Stock).CompareTo(b.Variants.Sum(v => v.Stock)),
            _ => (a, b) => Js.LocaleCompare(a.UpdatedAt ?? "", b.UpdatedAt ?? ""),
        };
        var direction = query.Dir == "asc" ? 1 : -1;
        rows = rows.Order(Comparer<Product>.Create((a, b) =>
        {
            var c = cmp(a, b);
            return (c != 0 ? c : Js.LocaleCompare(a.Id, b.Id)) * direction;
        })).ToList();
        var pageSize = Math.Max(1, query.PageSize);
        var page = Math.Min(Math.Max(1, query.Page), Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize)));
        return new Paged<AdminProductRow> { Total = rows.Count, Page = page, PageSize = pageSize, Items = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(ToRow).ToList() };
    }

    public async Task<List<AdminCategoryOption>> CategoriesAsync() =>
        (await mongo.Categories.Find(new BsonDocument("parentId", new BsonDocument("$exists", true))).ToListAsync()).Select(c => new AdminCategoryOption { Id = c.Id, Name = c.Name }).ToList();

    public async Task<AdminProductDetail> GetAsync(string id) =>
        ToDetail(await mongo.Products.Find(p => p.Id == id).FirstOrDefaultAsync() ?? throw new AppError(ApiErrorCode.NotFound, "Product not found"));

    public async Task<AdminProductDetail> CreateAsync(AdminProductInput input)
    {
        var categories = await mongo.Categories.Find(FilterDefinition<Category>.Empty).ToListAsync();
        var leafIds = categories.Where(c => c.ParentId is not null).Select(c => c.Id).ToHashSet();
        var existingVariants = (await mongo.Products.Find(FilterDefinition<Product>.Empty).Project(p => p.Variants).ToListAsync()).SelectMany(v => v).ToList();
        ValidateInput(input, leafIds, existingVariants);
        var leaf = categories.FirstOrDefault(c => c.Id == input.CategoryId);
        var parent = categories.FirstOrDefault(c => c.Id == leaf?.ParentId);
        if (leaf is null || parent is null) throw new AppError(ApiErrorCode.Validation, "Choose a category", new Dictionary<string, string> { ["categoryId"] = "Choose a category" });
        // There is no media upload pipeline yet; a same-category product's images stand in until real uploads exist.
        var donor = await mongo.Products.Find(p => p.CategoryId == leaf.Id).FirstOrDefaultAsync();
        var anyProduct = donor ?? await mongo.Products.Find(FilterDefinition<Product>.Empty).FirstOrDefaultAsync();
        if (anyProduct is null) throw new AppError(ApiErrorCode.Conflict, "Catalog is empty; seed it before creating products.");
        var id = $"p-new-{Ids.Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}";
        var doc = new Product
        {
            DocId = id,
            Id = id,
            Slug = $"{Slugify(input.Title)}-{id[^4..]}",
            Title = input.Title.Trim(),
            BrandId = $"brand-{Slugify(input.BrandName)}",
            BrandName = input.BrandName.Trim(),
            CategoryId = leaf.Id,
            CategoryPath =
            [
                new CategoryRef { Id = parent.Id, Slug = parent.Slug, Name = parent.Name },
                new CategoryRef { Id = leaf.Id, Slug = leaf.Slug, Name = leaf.Name },
            ],
            Description = Paragraphs(input.Description),
            Highlights = input.Highlights.Select(Str).ToList(),
            Images = anyProduct.Images,
            Attributes = new BsonDocument(),
            VariantAxes = input.Variants[0].Options.Keys.ToList(),
            Variants = BuildVariants(id, input, []),
            Rating = new RatingSummary { Average = 0, Count = 0, Distribution = [0, 0, 0, 0, 0] },
            Tags = input.Tags.Select(Str).ToList(),
            CreatedAt = NowIso(),
            Popularity = 0,
            Status = input.Status,
            UpdatedAt = NowIso(),
        };
        await mongo.Products.InsertOneAsync(doc);
        await SyncIndexAsync(doc);
        return ToDetail(doc);
    }

    public async Task<AdminProductDetail> UpdateAsync(string id, AdminProductInput input)
    {
        var existing = await mongo.Products.Find(p => p.Id == id).FirstOrDefaultAsync() ?? throw new AppError(ApiErrorCode.NotFound, "Product not found");
        var categories = await mongo.Categories.Find(FilterDefinition<Category>.Empty).ToListAsync();
        var leafIds = categories.Where(c => c.ParentId is not null).Select(c => c.Id).ToHashSet();
        var others = await mongo.Products.Find(p => p.Id != id).ToListAsync();
        ValidateInput(input, leafIds, others.SelectMany(o => o.Variants).ToList());
        var updated = existing.Shallow();
        updated.Title = input.Title.Trim();
        updated.BrandName = input.BrandName.Trim();
        updated.Description = Paragraphs(input.Description);
        updated.Highlights = input.Highlights.Select(Str).ToList();
        updated.Tags = input.Tags.Select(Str).ToList();
        updated.Variants = BuildVariants(existing.Id, input, existing.Variants);
        updated.Status = input.Status;
        updated.UpdatedAt = NowIso();
        await mongo.Products.ReplaceOneAsync(p => p.Id == id, updated);
        await SyncIndexAsync(updated);
        return ToDetail(updated);
    }

    public async Task<int> BulkSetStatusAsync(List<string> ids, string status)
    {
        var targets = await mongo.Products.Find(Builders<Product>.Filter.In(p => p.Id, ids)).ToListAsync();
        if (targets.Count == 0) return 0;
        var now = NowIso();
        await mongo.Products.UpdateManyAsync(Builders<Product>.Filter.In(p => p.Id, targets.Select(t => t.Id)), Builders<Product>.Update.Set(p => p.Status, status).Set(p => p.UpdatedAt, now));
        await Task.WhenAll(targets.Select(t =>
        {
            var copy = t.Shallow();
            copy.Status = status;
            copy.UpdatedAt = now;
            return SyncIndexAsync(copy);
        }));
        return targets.Count;
    }

    public async Task<int> BulkDeleteDraftsAsync(List<string> ids)
    {
        var drafts = await mongo.Products.Find(Builders<Product>.Filter.And(Builders<Product>.Filter.In(p => p.Id, ids), Builders<Product>.Filter.Eq(p => p.Status, "draft"))).ToListAsync();
        if (drafts.Count == 0) return 0;
        await mongo.Products.DeleteManyAsync(Builders<Product>.Filter.In(p => p.Id, drafts.Select(d => d.Id)));
        var tasks = new List<Task>();
        foreach (var d in drafts)
        {
            tasks.Add(search.DeleteProductAsync(d.Id).ContinueWith(_ => { }));
            tasks.Add(cache.InvalidateTagAsync($"product:{d.Id}"));
        }
        tasks.Add(cache.InvalidateTagAsync(CatalogService.ListingsTag));
        await Task.WhenAll(tasks);
        return drafts.Count;
    }
}
