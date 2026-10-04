using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EcomApi.Config;
using EcomApi.Domain;
using EcomApi.Http;

namespace EcomApi.Services.Catalog;

public sealed class FacetClause(string filter, string field)
{
    /// <summary>The Meilisearch filter expression for just this one facet group, e.g. brandSlug IN ["acme"].</summary>
    public string Filter { get; } = filter;
    /// <summary>The field this facet counts.</summary>
    public string Field { get; } = field;
}

public sealed class SearchQuery
{
    public string? Q { get; init; }
    /// <summary>Clauses that are never toggled off (category, brand and collection page scope). Always applied.</summary>
    public List<string> Scope { get; init; } = [];
    /// <summary>One clause per togglable facet group that currently has a selection.</summary>
    public List<FacetClause> FacetFilters { get; init; } = [];
    /// <summary>Every facet field to return counts for.</summary>
    public List<string> FacetFields { get; init; } = [];
    public string? PriceFilter { get; init; }
    public string Sort { get; init; } = "relevance";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 24;
}

public sealed class SearchResult
{
    public List<string> Ids { get; init; } = [];
    public int Total { get; init; }
    public Dictionary<string, Dictionary<string, int>> FacetDistribution { get; init; } = new();
    public PriceBounds PriceBounds { get; init; } = new();
}

/// <summary>The Meilisearch REST API, just the calls the shop needs.</summary>
public sealed class MeiliClient
{
    private readonly HttpClient http;

    public MeiliClient(ApiConfig config)
    {
        http = new HttpClient { BaseAddress = new Uri(config.MeiliUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        if (config.MeiliMasterKey.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.MeiliMasterKey);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Meilisearch {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async Task WaitForTaskAsync(JsonNode? task)
    {
        var uid = task?["taskUid"]?.GetValue<long>();
        if (uid is null) return;
        for (var i = 0; i < 600; i++)
        {
            var status = await SendAsync(HttpMethod.Get, $"tasks/{uid}");
            var state = status?["status"]?.GetValue<string>();
            if (state == "succeeded") return;
            if (state is "failed" or "canceled") throw new HttpRequestException($"Meilisearch task {uid} {state}: {status?["error"]?["message"]}");
            await Task.Delay(50);
        }
        throw new TimeoutException($"Meilisearch task {uid} did not finish");
    }

    public Task<JsonNode?> CreateIndexAsync(string uid, string primaryKey) => SendAsync(HttpMethod.Post, "indexes", new JsonObject { ["uid"] = uid, ["primaryKey"] = primaryKey });
    public Task<JsonNode?> UpdateSettingsAsync(string uid, JsonObject settings) => SendAsync(HttpMethod.Patch, $"indexes/{uid}/settings", settings);
    public Task<JsonNode?> AddDocumentsAsync(string uid, JsonArray documents) => SendAsync(HttpMethod.Post, $"indexes/{uid}/documents", documents);
    public Task<JsonNode?> DeleteDocumentAsync(string uid, string id) => SendAsync(HttpMethod.Delete, $"indexes/{uid}/documents/{Uri.EscapeDataString(id)}");
    public Task<JsonNode?> SearchAsync(string uid, JsonObject body) => SendAsync(HttpMethod.Post, $"indexes/{uid}/search", body);
    public Task<JsonNode?> HealthAsync() => SendAsync(HttpMethod.Get, "health");
}

/// <summary>Typo-tolerant, faceted product search over Meilisearch, behind a circuit breaker.</summary>
public sealed class SearchService
{
    /// <summary>Interchangeable words, so real search behaves the way a shopper already saw in development.</summary>
    private static readonly string[][] SynonymGroups =
    [
        ["tee", "tshirt"], ["mobile", "phone", "smartphone", "cellphone"], ["laptop", "notebook", "ultrabook"], ["tv", "television"], ["sofa", "couch"],
        ["sneaker", "shoe", "trainer"], ["earbud", "earphone", "headphone"], ["bag", "backpack"], ["kid", "child"], ["jean", "denim"],
    ];

    private static readonly Dictionary<string, string[]?> SortField = new()
    {
        ["relevance"] = null,
        ["featured"] = ["popularity:desc"],
        ["price-asc"] = ["priceMin:asc"],
        ["price-desc"] = ["priceMax:desc"],
        ["newest"] = ["createdAtTs:desc"],
        ["rating"] = ["ratingAverage:desc"],
        ["discount"] = ["bestDiscount:desc"],
    };

    private readonly MeiliClient client;
    private readonly CircuitBreaker breaker = new("meilisearch", new CircuitBreakerOptions(3, 20_000, 3_000));
    public string IndexName { get; }

    public SearchService(ApiConfig config)
    {
        client = new MeiliClient(config);
        IndexName = $"products{config.MeiliIndexSuffix}";
    }

    public CircuitStats BreakerStats() => breaker.Stats();

    /// <summary>
    /// Every shopper-facing read goes through this: a couple of quick retries absorb a transient blip (counted as one failure toward the
    /// breaker), and once Meilisearch is genuinely down the breaker fails fast with a clear, non-fatal error instead of every request
    /// queuing up behind a dead dependency.
    /// </summary>
    private async Task<T> GuardedAsync<T>(Func<Task<T>> fn)
    {
        try
        {
            return await breaker.ExecAsync(() => Retry.WithRetryAsync(fn, 2, 150));
        }
        catch (Exception error)
        {
            if (error is not CircuitOpenException) Log.Warn("SearchService", $"meilisearch call failed: {error.Message}");
            throw new AppError(ApiErrorCode.Network, "Search is temporarily unavailable. Please try again shortly.");
        }
    }

    /// <summary>Idempotent: safe to call every time a seed script runs.</summary>
    public async Task EnsureIndexAsync(IEnumerable<string> attrFields)
    {
        var attrs = attrFields.ToList();
        try
        {
            await client.WaitForTaskAsync(await client.CreateIndexAsync(IndexName, "id"));
        }
        catch (Exception)
        {
            // already exists is fine
        }
        string[] numeric = ["priceMin", "priceMax", "ratingAverage", "bestDiscount", "totalStock", "popularity", "createdAtTs"];
        var synonyms = new JsonObject();
        foreach (var group in SynonymGroups)
            foreach (var word in group)
                synonyms[word] = new JsonArray(group.Where(w => w != word).Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
        await client.WaitForTaskAsync(await client.UpdateSettingsAsync(IndexName, new JsonObject
        {
            ["searchableAttributes"] = new JsonArray(new[] { "title", "brandName", "tags" }.Concat(attrs).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["filterableAttributes"] = new JsonArray(new[] { "categoryId", "brandSlug", "tags" }.Concat(numeric).Concat(attrs).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["sortableAttributes"] = new JsonArray(numeric.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["synonyms"] = synonyms,
        }));
    }

    public async Task IndexProductsAsync(IEnumerable<JsonObject> docs)
    {
        var array = new JsonArray(docs.Select(d => (JsonNode?)d).ToArray());
        if (array.Count == 0) return;
        await client.WaitForTaskAsync(await client.AddDocumentsAsync(IndexName, array));
    }

    public async Task DeleteProductAsync(string id) => await client.WaitForTaskAsync(await client.DeleteDocumentAsync(IndexName, id));

    public async Task HealthAsync() => await client.HealthAsync();

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    public Task<SearchResult> SearchAsync(SearchQuery query) => GuardedAsync(async () =>
    {
        var allFilter = query.Scope.Concat(query.FacetFilters.Select(f => f.Filter)).Concat(query.PriceFilter is not null ? [query.PriceFilter] : []).ToList();
        var body = new JsonObject
        {
            ["q"] = query.Q ?? "",
            ["facets"] = Strings(query.FacetFields.Concat(["priceMin", "priceMax"])),
            ["page"] = Math.Max(1, query.Page),
            ["hitsPerPage"] = Math.Max(1, query.PageSize),
            ["showRankingScore"] = true,
        };
        if (allFilter.Count > 0) body["filter"] = string.Join(" AND ", allFilter);
        if (SortField.TryGetValue(query.Sort, out var sort) && sort is not null) body["sort"] = Strings(sort);
        var main = await client.SearchAsync(IndexName, body) ?? throw new HttpRequestException("empty search response");

        // For each active facet group, re-run with that group's own clause removed, so its own option counts reflect "if you added this
        // option" rather than "given you already selected it".
        var distribution = ReadDistribution(main["facetDistribution"]);
        await Task.WhenAll(query.FacetFilters.Select(async own =>
        {
            var without = allFilter.Where(f => f != own.Filter).ToList();
            var req = new JsonObject { ["q"] = query.Q ?? "", ["facets"] = Strings([own.Field]), ["limit"] = 0 };
            if (without.Count > 0) req["filter"] = string.Join(" AND ", without);
            var res = await client.SearchAsync(IndexName, req);
            var counts = res?["facetDistribution"]?[own.Field];
            if (counts is JsonObject obj)
                lock (distribution) distribution[own.Field] = obj.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>());
        }));

        var hits = main["hits"]?.AsArray() ?? [];
        var stats = main["facetStats"];
        return new SearchResult
        {
            Ids = hits.Select(h => h!["id"]!.GetValue<string>()).ToList(),
            Total = main["totalHits"]?.GetValue<int>() ?? hits.Count,
            FacetDistribution = distribution,
            PriceBounds = new PriceBounds { Min = stats?["priceMin"]?["min"]?.GetValue<double>() ?? 0, Max = stats?["priceMax"]?["max"]?.GetValue<double>() ?? 0 },
        };
    });

    private static Dictionary<string, Dictionary<string, int>> ReadDistribution(JsonNode? node)
    {
        var result = new Dictionary<string, Dictionary<string, int>>();
        if (node is JsonObject obj)
            foreach (var (field, counts) in obj)
                if (counts is JsonObject c) result[field] = c.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>());
        return result;
    }

    public Task<List<string>> SuggestProductsAsync(string q, int limit) => GuardedAsync(async () =>
    {
        var res = await client.SearchAsync(IndexName, new JsonObject { ["q"] = q, ["limit"] = limit });
        return (res?["hits"]?.AsArray() ?? []).Select(h => h!["id"]!.GetValue<string>()).ToList();
    });

    /// <summary>Exact count of documents matching the filters (ANDed): page and hitsPerPage make Meilisearch compute an exact total.</summary>
    public Task<int> CountAsync(IEnumerable<string> filters) => GuardedAsync(async () =>
    {
        var list = filters.ToList();
        var body = new JsonObject { ["q"] = "", ["page"] = 1, ["hitsPerPage"] = 1 };
        if (list.Count > 0) body["filter"] = string.Join(" AND ", list);
        var res = await client.SearchAsync(IndexName, body);
        return res?["totalHits"]?.GetValue<int>() ?? 0;
    });
}
