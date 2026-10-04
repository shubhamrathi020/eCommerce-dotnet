using EcomApi.Config;
using EcomApi.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace EcomApi.Services.Catalog;

/// <summary>
/// The catalog store: MongoDB, chosen over Postgres for products because attributes and variant options differ freely by category.
/// The native driver is used directly, so each document is the shared model shape and the id is the same string id used everywhere.
/// </summary>
public sealed class MongoService(ApiConfig config)
{
    private MongoClient? client;
    private static bool conventionsRegistered;
    private static readonly object RegisterLock = new();

    /// <summary>camelCase element names, no explicit nulls written, unknown stored fields tolerated.</summary>
    private static void RegisterConventions()
    {
        lock (RegisterLock)
        {
            if (conventionsRegistered) return;
            ConventionRegistry.Register("ecom", new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new IgnoreIfNullConvention(true),
                new IgnoreExtraElementsConvention(true),
            }, t => t.Namespace == typeof(Product).Namespace);
            conventionsRegistered = true;
        }
    }

    public async Task InitAsync()
    {
        RegisterConventions();
        client = new MongoClient(config.MongoUrl);
        await Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
    }

    public Task CloseAsync()
    {
        client?.Dispose();
        return Task.CompletedTask;
    }

    private IMongoDatabase Database => (client ?? throw new InvalidOperationException("MongoDB is not connected")).GetDatabase(config.MongoDbName);

    public Task PingAsync() => Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

    public IMongoCollection<Product> Products => Database.GetCollection<Product>("products");
    public IMongoCollection<Category> Categories => Database.GetCollection<Category>("categories");
    public IMongoCollection<Brand> Brands => Database.GetCollection<Brand>("brands");
    public IMongoCollection<Collection> Collections => Database.GetCollection<Collection>("collections");
    public IMongoCollection<Review> Reviews => Database.GetCollection<Review>("reviews");
    public IMongoCollection<HomeDoc> Home => Database.GetCollection<HomeDoc>("home");
}
