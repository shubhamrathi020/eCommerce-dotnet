using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EcomApi.Domain;

// The data shapes the API exchanges with the frontend (the C# port of @ecom/contracts). Classes that are also stored in
// MongoDB carry the document id and admin-only bookkeeping as [JsonIgnore] members, so nothing but the contract itself
// reaches a shopper.

public sealed class ImageRef
{
    public string Url { get; set; } = "";
    public string Alt { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class Brand
{
    [BsonId, JsonIgnore] public string? DocId { get; set; }
    [BsonElement("id")] public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    [BsonExtraElements, JsonIgnore] public BsonDocument? Extra { get; set; }
}

public sealed class Collection
{
    [BsonId, JsonIgnore] public string? DocId { get; set; }
    [BsonElement("id")] public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tag { get; set; } = "";
    [BsonExtraElements, JsonIgnore] public BsonDocument? Extra { get; set; }
}

/// <summary>Present when a variant can be bought at zero stock.</summary>
public sealed class Backorder
{
    public string? ExpectedDate { get; set; }
}

public sealed class Variant
{
    [BsonElement("id")] public string Id { get; set; } = "";
    public string Sku { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = new();
    public Money Price { get; set; } = new();
    public Money? Mrp { get; set; }
    public long Stock { get; set; }
    public Backorder? Backorder { get; set; }
    public List<ImageRef>? Images { get; set; }
}

public sealed class RatingSummary
{
    public double Average { get; set; }
    public long Count { get; set; }
    public long[] Distribution { get; set; } = new long[5];
}

public sealed class CategoryRef
{
    [BsonElement("id")] public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Seo
{
    public string? Title { get; set; }
    public string? Description { get; set; }
}

public enum ProductStatus
{
    Draft,
    Published,
    Archived,
}

/// <summary>The full catalog product shown to shoppers, and the MongoDB document it is stored as.</summary>
public sealed class Product
{
    [BsonId, JsonIgnore] public string? DocId { get; set; }
    [BsonElement("id")] public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string BrandId { get; set; } = "";
    public string BrandName { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public List<CategoryRef> CategoryPath { get; set; } = new();
    public string Description { get; set; } = "";
    public List<string> Highlights { get; set; } = new();
    public List<ImageRef> Images { get; set; } = new();
    public BsonDocument Attributes { get; set; } = new();
    public List<string> VariantAxes { get; set; } = new();
    public List<Variant> Variants { get; set; } = new();
    public RatingSummary Rating { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string CreatedAt { get; set; } = "";
    public double Popularity { get; set; }
    public List<string>? SlugHistory { get; set; }
    public string? SellerId { get; set; }
    public Seo? Seo { get; set; }

    /// <summary>Admin bookkeeping: shoppers only ever see "published" products.</summary>
    [JsonIgnore] public string? Status { get; set; }
    [JsonIgnore] public string? UpdatedAt { get; set; }
    /// <summary>Whatever else the stored document carries; kept so an admin save never drops fields it does not know.</summary>
    [BsonExtraElements, JsonIgnore] public BsonDocument? Extra { get; set; }

    public Product Shallow() => (Product)MemberwiseClone();
}

public enum StockStatus
{
    InStock,
    LowStock,
    OutOfStock,
    BackOrder,
}

public sealed class SummaryRate
{
    public double Average { get; set; }
    public long Count { get; set; }
}

/// <summary>The lightweight shape for cards and lists.</summary>
public sealed class ProductSummary
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string BrandName { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public ImageRef Image { get; set; } = new();
    public ImageRef? HoverImage { get; set; }
    public Money PriceMin { get; set; } = new();
    public Money PriceMax { get; set; } = new();
    public Money? MrpMin { get; set; }
    public SummaryRate Rating { get; set; } = new();
    public string StockStatus { get; set; } = "";
    public long? StockLeft { get; set; }
    public int VariantCount { get; set; }
    public string? QuickAddVariantId { get; set; }
}

public sealed class AttributeDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Unit { get; set; }
    public bool Filterable { get; set; }
    public bool VariantAxis { get; set; }
    public List<string>? Values { get; set; }
}

public class Category
{
    [BsonId, JsonIgnore] public string? DocId { get; set; }
    [BsonElement("id")] public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public string? ImageUrl { get; set; }
    public int Order { get; set; }
    public List<AttributeDef>? AttributeDefs { get; set; }
    [BsonExtraElements, JsonIgnore] public BsonDocument? Extra { get; set; }
}

/// <summary>A category with resolved children (the shape of GET /catalog/categories/tree).</summary>
public sealed class CategoryNode
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public string? ImageUrl { get; set; }
    public int Order { get; set; }
    public List<AttributeDef>? AttributeDefs { get; set; }
    public List<CategoryNode> Children { get; set; } = new();

    public static CategoryNode From(Category c) => new() { Id = c.Id, Slug = c.Slug, Name = c.Name, ParentId = c.ParentId, ImageUrl = c.ImageUrl, Order = c.Order, AttributeDefs = c.AttributeDefs };
}

public sealed class Review
{
    [BsonId, JsonIgnore] public string? DocId { get; set; }
    [BsonElement("id")] public string Id { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Author { get; set; } = "";
    public int Rating { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public bool Verified { get; set; }
    public int Helpful { get; set; }
    public string? Status { get; set; }
    public bool? Mine { get; set; }
    public bool? Voted { get; set; }
    [BsonExtraElements, JsonIgnore] public BsonDocument? Extra { get; set; }
}

public sealed class ReviewPage
{
    public List<Review> Items { get; set; } = new();
    public int Total { get; set; }
    public RatingSummary Summary { get; set; } = new();
}

public sealed class Serviceability
{
    public bool Serviceable { get; set; }
    public string Pincode { get; set; } = "";
    public int? EstimatedDays { get; set; }
    public string? EstimatedDate { get; set; }
    public bool? CodAvailable { get; set; }
}

public sealed class FacetOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public bool Selected { get; set; }
}

public sealed class Facet
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public List<FacetOption> Options { get; set; } = new();
}

public sealed class PriceBounds
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class ListingResult
{
    public List<ProductSummary> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<Facet> Facets { get; set; } = new();
    public PriceBounds PriceBounds { get; set; } = new();
    public string Title { get; set; } = "";
    public List<CategoryRef> Breadcrumb { get; set; } = new();
    public string? CorrectedFrom { get; set; }
}

/// <summary>A parsed listing request.</summary>
public sealed class ListingQuery
{
    public string? CategorySlug { get; set; }
    public string? BrandSlug { get; set; }
    public string? CollectionSlug { get; set; }
    public string? Q { get; set; }
    public Dictionary<string, List<string>> Filters { get; set; } = new();
    public long? PriceMin { get; set; }
    public long? PriceMax { get; set; }
    public string Sort { get; set; } = "relevance";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 24;
}

public sealed class NameSlug
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class SearchSuggestions
{
    public List<string> Queries { get; set; } = new();
    public List<ProductSummary> Products { get; set; } = new();
    public List<NameSlug> Categories { get; set; } = new();
    public List<NameSlug> Brands { get; set; } = new();
}

/// <summary>The storefront home page. Banners and tiles are stored and passed through untouched.</summary>
public sealed class HomeData
{
    public BsonArray Banners { get; set; } = new();
    public BsonArray CategoryTiles { get; set; } = new();
    public HomeDeals Deals { get; set; } = new();
    public List<ProductRow> Rows { get; set; } = new();
    public List<Brand> Brands { get; set; } = new();
}

public sealed class HomeDeals
{
    public string EndsAt { get; set; } = "";
    public List<ProductSummary> Items { get; set; } = new();
}

public sealed class ProductRow
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Link { get; set; }
    public List<ProductSummary> Items { get; set; } = new();
}

/// <summary>The stored home document.</summary>
public sealed class HomeDoc
{
    [BsonId] public string DocId { get; set; } = "home";
    public BsonArray? Banners { get; set; }
    public BsonArray? CategoryTiles { get; set; }
    [BsonExtraElements] public BsonDocument? Extra { get; set; }
}
