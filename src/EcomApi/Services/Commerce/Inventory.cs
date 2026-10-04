using EcomApi.Cache;
using EcomApi.Http;
using EcomApi.Services.Catalog;
using MongoDB.Bson;
using MongoDB.Driver;
using EcomApi.Domain;

namespace EcomApi.Services.Commerce;

public sealed record StockLine(string VariantId, long Quantity);

/// <summary>
/// Atomic stock movement directly on the catalog store. A variant's stock field in MongoDB is the one source of truth. Each line is
/// decremented with a single conditional update, which MongoDB guarantees is atomic per document: two concurrent shoppers can never both
/// take the last unit. Lines of one order are not atomic as a group (a standalone MongoDB has no multi-document transactions), so if a
/// later line fails the earlier ones are rolled back with compensating increments: it never oversells.
/// </summary>
public sealed class InventoryService(MongoService mongo, CacheService cache)
{
    /// <summary>
    /// Atomically takes the units of one variant; throws if not enough is left. The positional operator only exists in the update document,
    /// so the "enough stock" condition lives inside the element match alongside the id.
    /// </summary>
    private async Task TakeOneAsync(StockLine line)
    {
        var filter = new BsonDocument("variants", new BsonDocument("$elemMatch", new BsonDocument { ["id"] = line.VariantId, ["stock"] = new BsonDocument("$gte", line.Quantity) }));
        var update = new BsonDocument("$inc", new BsonDocument("variants.$.stock", -line.Quantity));
        var result = await mongo.Products.UpdateOneAsync(filter, update);
        if (result.MatchedCount == 0) throw new AppError(ApiErrorCode.Validation, "Some items in your cart are no longer available.");
        await InvalidateCachesAsync(line.VariantId);
    }

    private async Task GiveBackOneAsync(StockLine line)
    {
        await mongo.Products.UpdateOneAsync(new BsonDocument("variants.id", line.VariantId), new BsonDocument("$inc", new BsonDocument("variants.$.stock", line.Quantity)));
        await InvalidateCachesAsync(line.VariantId);
    }

    /// <summary>An order moving stock is exactly as much a catalog write as an admin edit: cached product pages and listings go stale otherwise.</summary>
    private Task InvalidateCachesAsync(string variantId) => Task.WhenAll(cache.InvalidateTagAsync($"variant:{variantId}"), cache.InvalidateTagAsync(CatalogService.ListingsTag));

    /// <summary>Decrements every line, or none: rolls back whatever already succeeded if a later line fails.</summary>
    public async Task TakeAsync(IEnumerable<StockLine> lines)
    {
        var taken = new List<StockLine>();
        try
        {
            foreach (var line in lines)
            {
                await TakeOneAsync(line);
                taken.Add(line);
            }
        }
        catch (Exception)
        {
            await Task.WhenAll(taken.Select(async line =>
            {
                try
                {
                    await GiveBackOneAsync(line);
                }
                catch (Exception)
                {
                    // best effort
                }
            }));
            throw;
        }
    }

    /// <summary>Returns stock, e.g. on cancellation or a payment-window timeout.</summary>
    public Task GiveBackAsync(IEnumerable<StockLine> lines) => Task.WhenAll(lines.Select(GiveBackOneAsync));
}
