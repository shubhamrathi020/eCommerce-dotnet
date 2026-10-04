using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EcomApi.Cache;
using EcomApi.Domain;
using EcomApi.Http;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EcomApi.Services.Catalog;

public sealed class ProductPage
{
    public Product Product { get; set; } = new();
    public string? RedirectedFrom { get; set; }
}

public sealed class ReviewQuery
{
    public string Sort { get; set; } = "recent";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public static class Js
{
    /// <summary>String#localeCompare for a locale-aware (not ordinal) ordering.</summary>
    public static int LocaleCompare(string a, string b) => string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.None);

    /// <summary>JSON.stringify of a string (quotes and escapes), used to build Meilisearch filter expressions.</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value, Json.Web);
}

public sealed class CatalogService(MongoService mongo, SearchService search, CacheService cache)
{
    private static readonly int[] RatingSteps = [4, 3, 2, 1];
    private static readonly int[] DiscountSteps = [10, 25, 40];

    /// <summary>
    /// Every home and listing entry carries this tag, so any catalog write can invalidate all of them at once without tracking exactly which
    /// listing queries happened to include the changed product. A single product's own page is invalidated precisely, by its own id.
    /// </summary>
    public const string ListingsTag = "catalog:listings";

    /// <summary>Deterministic JSON: object keys sorted recursively, so two logically identical queries produce the same cache key.</summary>
    private static string StableStringify(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                return "[" + string.Join(",", value.EnumerateArray().Select(StableStringify)) + "]";
            case JsonValueKind.Object:
                var entries = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Create(CultureInfo.InvariantCulture, false));
                return "{" + string.Join(",", entries.Select(p => $"{JsonSerializer.Serialize(p.Name)}:{StableStringify(p.Value)}")) + "}";
            default:
                return value.GetRawText();
        }
    }

    private static HashSet<string> LeafIdsUnder(Category category, List<Category> categories)
    {
        var children = categories.Where(c => c.ParentId == category.Id).ToList();
        return children.Count > 0 ? children.Select(c => c.Id).ToHashSet() : [category.Id];
    }

    private async Task<List<Product>> AllProductsAsync() => await mongo.Products.Find(p => p.Status == "published").ToListAsync();

    public Task<HomeData> HomeAsync() => cache.GetOrSetAsync("catalog:home", 60, _ => [ListingsTag], HomeUncachedAsync);

    private async Task<HomeData> HomeUncachedAsync()
    {
        var productsTask = AllProductsAsync();
        var brandsTask = mongo.Brands.Find(FilterDefinition<Brand>.Empty).SortBy(b => b.Name).Limit(12).ToListAsync();
        var homeTask = mongo.Home.Find(h => h.DocId == "home").FirstOrDefaultAsync();
        await Task.WhenAll(productsTask, brandsTask, homeTask);
        var products = productsTask.Result;
        var inStock = products.Where(p => Derive.StockStatusOf(p).Status != Stock.OutOfStock).ToList();
        List<ProductSummary> Summaries(IEnumerable<Product> list, int n) => list.Take(n).Select(Derive.ToSummary).ToList();
        var now = DateTimeOffset.UtcNow;
        var endOfDay = new DateTimeOffset(now.Year, now.Month, now.Day, 23, 59, 59, TimeSpan.Zero);
        var deals = inStock.OrderByDescending(Derive.BestDiscount).ToList();
        var home = homeTask.Result;
        return new HomeData
        {
            Banners = home?.Banners ?? [],
            CategoryTiles = home?.CategoryTiles ?? [],
            Deals = new HomeDeals { EndsAt = Derive.Iso(endOfDay), Items = Summaries(deals, 10) },
            Rows =
            [
                new ProductRow { Key = "featured", Title = "Featured for you", Items = Summaries(Derive.SortProducts(inStock, "featured"), 12) },
                new ProductRow { Key = "new", Title = "New arrivals", Link = "/collections/trending", Items = Summaries(Derive.SortProducts(inStock, "newest"), 12) },
                new ProductRow { Key = "best", Title = "Top rated", Items = Summaries(Derive.SortProducts(inStock.Where(p => p.Rating.Count >= 5), "rating"), 12) },
            ],
            Brands = brandsTask.Result,
        };
    }

    public Task<ListingResult> ListingAsync(ListingQuery query)
    {
        // A stable key regardless of key order in the filters: two requests for "the same" listing must always hit the same cache entry.
        var element = JsonSerializer.SerializeToElement(query, Json.Web);
        var key = $"catalog:listing:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(StableStringify(element))))}";
        return cache.GetOrSetAsync(key, 30, _ => [ListingsTag], () => ListingUncachedAsync(query));
    }

    private abstract record Group(string Field);

    private sealed record Categorical(string Field, Func<List<string>, string> BuildFilter) : Group(Field);

    private sealed record Threshold(string Field, int[] Steps, string Op) : Group(Field);

    private async Task<ListingResult> ListingUncachedAsync(ListingQuery query)
    {
        var categories = await mongo.Categories.Find(FilterDefinition<Category>.Empty).ToListAsync();
        var scope = new List<string>();
        var heading = "All products";
        var breadcrumb = new List<CategoryRef>();
        var attributeDefs = new List<AttributeDef>();

        if (!string.IsNullOrEmpty(query.CategorySlug))
        {
            var category = categories.FirstOrDefault(c => c.Slug == query.CategorySlug) ?? throw new AppError(ApiErrorCode.NotFound, "Category not found");
            var ids = LeafIdsUnder(category, categories);
            scope.Add($"categoryId IN [{string.Join(",", ids.Select(Js.Quote))}]");
            heading = category.Name;
            var parent = category.ParentId is not null ? categories.FirstOrDefault(c => c.Id == category.ParentId) : null;
            if (parent is not null) breadcrumb.Add(new CategoryRef { Id = parent.Id, Slug = parent.Slug, Name = parent.Name });
            breadcrumb.Add(new CategoryRef { Id = category.Id, Slug = category.Slug, Name = category.Name });
            if (category.AttributeDefs is not null) attributeDefs = category.AttributeDefs.Where(d => d.Filterable).ToList();
        }
        if (!string.IsNullOrEmpty(query.BrandSlug))
        {
            var brand = await mongo.Brands.Find(b => b.Slug == query.BrandSlug).FirstOrDefaultAsync() ?? throw new AppError(ApiErrorCode.NotFound, "Brand not found");
            scope.Add($"brandSlug = {Js.Quote(query.BrandSlug)}");
            heading = brand.Name;
        }
        if (!string.IsNullOrEmpty(query.CollectionSlug))
        {
            var collection = await mongo.Collections.Find(c => c.Slug == query.CollectionSlug).FirstOrDefaultAsync() ?? throw new AppError(ApiErrorCode.NotFound, "Collection not found");
            scope.Add($"tags = {Js.Quote(collection.Tag)}");
            heading = collection.Name;
        }

        var sel = query.Filters;
        static string InList(string field, List<string> values) => $"{field} IN [{string.Join(",", values.Select(Js.Quote))}]";
        var groups = new Dictionary<string, Group>
        {
            ["brand"] = new Categorical("brandSlug", v => InList("brandSlug", v)),
            ["rating"] = new Threshold("ratingAverage", RatingSteps, ">="),
            ["discount"] = new Threshold("bestDiscount", DiscountSteps, ">="),
            ["availability"] = new Threshold("totalStock", [1], ">="),
        };
        foreach (var def in attributeDefs)
        {
            var field = $"attr_{def.Key}";
            groups[def.Key] = new Categorical(field, v => InList(field, v));
        }

        var facetFilters = new List<FacetClause>();
        foreach (var (key, group) in groups)
        {
            if (!sel.TryGetValue(key, out var chosen) || chosen.Count == 0) continue;
            if (group is Categorical categorical) facetFilters.Add(new FacetClause(categorical.BuildFilter(chosen), categorical.Field));
            else if (group is Threshold threshold)
            {
                var min = chosen.Select(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).Min();
                facetFilters.Add(new FacetClause($"{threshold.Field} {threshold.Op} {(double.IsNaN(min) ? "NaN" : min.ToString("R", CultureInfo.InvariantCulture))}", threshold.Field));
            }
        }
        string? priceFilter = null;
        if (query.PriceMin is not null || query.PriceMax is not null)
        {
            var lo = query.PriceMin ?? 0;
            var hi = query.PriceMax ?? 9007199254740991L;
            priceFilter = $"priceMin <= {hi} AND priceMax >= {lo}";
        }

        var categoricalFacetFields = new[] { "brandSlug" }.Concat(attributeDefs.Select(d => $"attr_{d.Key}")).ToList();
        var result = await search.SearchAsync(new SearchQuery
        {
            Q = query.Q,
            Scope = scope,
            FacetFilters = facetFilters,
            FacetFields = categoricalFacetFields,
            PriceFilter = priceFilter,
            Sort = !string.IsNullOrWhiteSpace(query.Q) ? "relevance" : query.Sort,
            Page = query.Page,
            PageSize = query.PageSize,
        });
        // Meilisearch's own typo tolerance resolves small misspellings inside the main query itself, so there is no separate "corrected from" pass.
        var title = !string.IsNullOrWhiteSpace(query.Q) ? $"Results for \"{query.Q!.Trim()}\"" : heading;

        List<string> AllSelectedExceptField(string excludeField) => scope.Concat(facetFilters.Where(f => f.Field != excludeField).Select(f => f.Filter)).Concat(priceFilter is not null ? [priceFilter] : []).ToList();

        // Brand facet
        var brandCounts = result.FacetDistribution.GetValueOrDefault("brandSlug") ?? new Dictionary<string, int>();
        var brandDocs = await mongo.Brands.Find(Builders<Brand>.Filter.In(b => b.Slug, brandCounts.Keys)).ToListAsync();
        string BrandName(string slug) => brandDocs.FirstOrDefault(b => b.Slug == slug)?.Name ?? slug;
        var facets = new List<Facet>
        {
            new()
            {
                Key = "brand",
                Label = "Brand",
                Options = brandCounts
                    .Select(kv => new FacetOption { Value = kv.Key, Label = BrandName(kv.Key), Count = kv.Value, Selected = sel.TryGetValue("brand", out var b) && b.Contains(kv.Key) })
                    .OrderByDescending(o => o.Count).ThenBy(o => o.Label, Comparer<string>.Create(Js.LocaleCompare)).ToList(),
            },
        };
        foreach (var def in attributeDefs)
        {
            var counts = result.FacetDistribution.GetValueOrDefault($"attr_{def.Key}") ?? new Dictionary<string, int>();
            var options = counts.Select(kv => new FacetOption { Value = kv.Key, Label = kv.Key, Count = kv.Value, Selected = sel.TryGetValue(def.Key, out var chosen) && chosen.Contains(kv.Key) }).ToList();
            options.Sort((a, b) => def.Values is not null ? def.Values.IndexOf(a.Value) - def.Values.IndexOf(b.Value) : Js.LocaleCompare(a.Label, b.Label));
            if (options.Count > 1 || options.Any(o => o.Selected)) facets.Add(new Facet { Key = def.Key, Label = def.Label, Options = options });
        }
        // Threshold facets (rating, discount, availability): one exact count query per step, ignoring that group's own filter.
        bool Chosen(string key, string value) => sel.TryGetValue(key, out var c) && c.Contains(value);
        var ratingBase = AllSelectedExceptField("ratingAverage");
        var ratingCounts = await Task.WhenAll(RatingSteps.Select(n => search.CountAsync([.. ratingBase, $"ratingAverage >= {n}"])));
        facets.Add(new Facet { Key = "rating", Label = "Customer rating", Options = RatingSteps.Select((n, i) => new FacetOption { Value = n.ToString(CultureInfo.InvariantCulture), Label = $"{n} stars & up", Count = ratingCounts[i], Selected = Chosen("rating", n.ToString(CultureInfo.InvariantCulture)) }).ToList() });
        var discountBase = AllSelectedExceptField("bestDiscount");
        var discountCounts = await Task.WhenAll(DiscountSteps.Select(n => search.CountAsync([.. discountBase, $"bestDiscount >= {n}"])));
        facets.Add(new Facet { Key = "discount", Label = "Discount", Options = DiscountSteps.Select((n, i) => new FacetOption { Value = n.ToString(CultureInfo.InvariantCulture), Label = $"{n}% or more", Count = discountCounts[i], Selected = Chosen("discount", n.ToString(CultureInfo.InvariantCulture)) }).ToList() });
        var availabilityBase = AllSelectedExceptField("totalStock");
        var inStockCount = await search.CountAsync([.. availabilityBase, "totalStock >= 1"]);
        facets.Add(new Facet { Key = "availability", Label = "Availability", Options = [new FacetOption { Value = "in_stock", Label = "In stock only", Count = inStockCount, Selected = Chosen("availability", "in_stock") }] });

        var pageSize = Math.Max(1, query.PageSize);
        var pages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)pageSize));
        var page = Math.Min(Math.Max(1, query.Page), pages);
        var products = await mongo.Products.Find(Builders<Product>.Filter.In(p => p.Id, result.Ids)).ToListAsync();
        var byId = products.ToDictionary(p => p.Id);
        // Meilisearch already returned ids in the right order; Mongo's $in does not preserve it.
        var items = result.Ids.Where(byId.ContainsKey).Select(id => Derive.ToSummary(byId[id])).ToList();

        return new ListingResult { Items = items, Total = result.Total, Page = page, PageSize = pageSize, Facets = facets, PriceBounds = result.PriceBounds, Title = title, Breadcrumb = breadcrumb };
    }

    public Task<ProductPage> ProductAsync(string slug) => cache.GetOrSetAsync(
        $"catalog:product:{slug}",
        120,
        // Tagged per variant too, so stock changes (which only know a variant id) can invalidate this exact page precisely.
        result => new[] { ListingsTag, $"product:{result.Product.Id}" }.Concat(result.Product.Variants.Select(v => $"variant:{v.Id}")),
        () => ProductUncachedAsync(slug));

    private async Task<ProductPage> ProductUncachedAsync(string slug)
    {
        var direct = await mongo.Products.Find(p => p.Slug == slug && p.Status == "published").FirstOrDefaultAsync();
        if (direct is not null) return new ProductPage { Product = direct };
        var moved = await mongo.Products.Find(Builders<Product>.Filter.And(Builders<Product>.Filter.AnyEq(p => p.SlugHistory, slug), Builders<Product>.Filter.Eq(p => p.Status, "published"))).FirstOrDefaultAsync();
        if (moved is not null) return new ProductPage { Product = moved, RedirectedFrom = slug };
        throw new AppError(ApiErrorCode.NotFound, "Product not found");
    }

    public async Task<List<Product>> ProductsByIdsAsync(List<string> ids)
    {
        if (ids.Count == 0) return [];
        var docs = await mongo.Products.Find(Builders<Product>.Filter.And(Builders<Product>.Filter.In(p => p.Id, ids), Builders<Product>.Filter.Eq(p => p.Status, "published"))).ToListAsync();
        var byId = docs.ToDictionary(d => d.Id, d => d);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public async Task<List<ProductSummary>> SummariesByIdsAsync(List<string> ids) => (await ProductsByIdsAsync(ids)).Select(Derive.ToSummary).ToList();

    public async Task<List<ProductSummary>> RelatedAsync(string productId)
    {
        var product = await mongo.Products.Find(p => p.Id == productId && p.Status == "published").FirstOrDefaultAsync();
        if (product is null) return [];
        var rootId = product.CategoryPath[0].Id;
        var filter = new BsonDocument
        {
            ["id"] = new BsonDocument("$ne", productId),
            ["status"] = "published",
            ["$or"] = new BsonArray { new BsonDocument("categoryId", product.CategoryId), new BsonDocument("categoryPath.0.id", rootId) },
        };
        var candidates = await mongo.Products.Find(filter).ToListAsync();
        var sameLeaf = candidates.Where(p => p.CategoryId == product.CategoryId).ToList();
        var sameRoot = candidates.Where(p => p.CategoryId != product.CategoryId).Take(4).ToList();
        return Derive.SortProducts(sameLeaf.Concat(sameRoot), "featured").Take(8).Select(Derive.ToSummary).ToList();
    }

    public async Task<List<ProductSummary>> BoughtTogetherAsync(string productId)
    {
        var product = await mongo.Products.Find(p => p.Id == productId && p.Status == "published").FirstOrDefaultAsync();
        if (product is null) return [];
        var rootId = product.CategoryPath[0].Id;
        var filter = new BsonDocument { ["categoryPath.0.id"] = rootId, ["categoryId"] = new BsonDocument("$ne", product.CategoryId), ["status"] = "published" };
        var others = (await mongo.Products.Find(filter).ToListAsync()).Where(p => Derive.StockStatusOf(p).Status != Stock.OutOfStock);
        return Derive.SortProducts(others, "featured").Take(2).Select(Derive.ToSummary).ToList();
    }

    public async Task<ReviewPage> ReviewsAsync(string productId, ReviewQuery query)
    {
        var product = await mongo.Products.Find(p => p.Id == productId && p.Status == "published").FirstOrDefaultAsync() ?? throw new AppError(ApiErrorCode.NotFound, "Product not found");
        var all = await mongo.Reviews.Find(r => r.ProductId == productId).ToListAsync();
        Comparison<Review> sorter = query.Sort switch
        {
            "helpful" => (a, b) => b.Helpful.CompareTo(a.Helpful),
            "high" => (a, b) => b.Rating.CompareTo(a.Rating),
            "low" => (a, b) => a.Rating.CompareTo(b.Rating),
            _ => (a, b) => Js.LocaleCompare(b.CreatedAt, a.CreatedAt),
        };
        // List.Sort is unstable; OrderBy with a comparer is stable like Array#sort.
        var sorted = all.Order(Comparer<Review>.Create(sorter)).ToList();
        var start = (Math.Max(1, query.Page) - 1) * query.PageSize;
        return new ReviewPage { Items = sorted.Skip(start).Take(query.PageSize).ToList(), Total = sorted.Count, Summary = product.Rating };
    }

    public Serviceability Serviceability(string pincode)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pincode, "^[1-9][0-9]{5}$"))
            throw new AppError(ApiErrorCode.Validation, "Enter a valid 6-digit pin code", new Dictionary<string, string> { ["pincode"] = "Invalid pin code" });
        return Derive.ComputeServiceability(pincode, DateTimeOffset.UtcNow);
    }

    public Task<List<CategoryNode>> CategoryTreeAsync() =>
        // Categories change far less often than products, so a longer TTL; still under the broad listings bucket since a rename affects breadcrumbs.
        cache.GetOrSetAsync("catalog:category-tree", 300, _ => [ListingsTag, "catalog:categories"], CategoryTreeUncachedAsync);

    private async Task<List<CategoryNode>> CategoryTreeUncachedAsync()
    {
        var flat = await mongo.Categories.Find(FilterDefinition<Category>.Empty).ToListAsync();
        var nodes = flat.ToDictionary(c => c.Id, CategoryNode.From);
        var roots = new List<CategoryNode>();
        foreach (var node in nodes.Values)
        {
            if (node.ParentId is not null && nodes.TryGetValue(node.ParentId, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        List<CategoryNode> Sort(List<CategoryNode> list)
        {
            var ordered = list.OrderBy(n => n.Order).ToList();
            foreach (var n in ordered) n.Children = Sort(n.Children);
            return ordered;
        }
        return Sort(roots);
    }
}
