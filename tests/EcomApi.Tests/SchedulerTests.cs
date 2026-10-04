using EcomApi.Cache;
using EcomApi.Config;
using EcomApi.Data;
using EcomApi.Services.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EcomApi.Tests;

/// <summary>
/// The abandoned-cart job needs the test PostgreSQL (the same one the Nest API's specs use, already migrated by eCommerce-api); the test
/// does nothing when it is not reachable. The shared integration specs cannot reach this job, as it only runs inside the server.
/// </summary>
public class SchedulerTests
{
    private const string Url = "postgresql://ecommerce:ecommerce@localhost:5432/ecommerce_test?schema=public";

    [Fact]
    public async Task RemindsASignedInCustomerAboutAnAbandonedCartOnceAndStaysQuietForAFreshOne()
    {
        var env = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = Environment.GetEnvironmentVariable("TEST_DATABASE_URL") ?? Url, ["MONGODB_URL"] = "mongodb://x", ["MEILI_URL"] = "http://x", ["REDIS_URL"] = "redis://x", ["RABBITMQ_URL"] = "amqp://x",
            ["JWT_ACCESS_SECRET"] = new string('a', 32), ["JWT_REFRESH_SECRET"] = new string('b', 32), ["NODE_ENV"] = "test",
        };
        var config = ApiConfig.Load(env);
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_ => EcomDb.CreateDataSource(config.DatabaseUrl));
        services.AddDbContext<EcomDb>((sp, o) => o.UseNpgsql(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), n => n.MapEnum<RoleKind>("Role", nameTranslator: EcomDb.NoTranslation)));
        services.AddScoped<OutboxService>();
        await using var provider = services.BuildServiceProvider();
        using var setup = provider.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<EcomDb>();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM carts LIMIT 1");
        }
        catch (Exception)
        {
            return; // no test database here
        }

        var email = $"rina-dotnet-test-{DateTime.UtcNow.Ticks}@example.com";
        var user = new UserRow { Id = Ids.NewId(), Name = "Rina Shah", Email = email, PasswordHash = "hash", VerifyTokenHash = $"verify-{Ids.NewId()}" };
        db.Users.Add(user);
        var ownerKey = $"user:{user.Id}";
        db.Carts.Add(new CartRow { Id = Ids.NewId(), OwnerKey = ownerKey, Items = """[{"variantId":"p-0002-v1","quantity":2,"seenPrice":100}]""" });
        await db.SaveChangesAsync();
        try
        {
            var scheduler = new SchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), null!, config);
            async Task<int> Reminders()
            {
                using var scope = provider.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<EcomDb>().Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM outbox_events WHERE \"routingKey\" = 'cart.abandoned' AND payload->>'email' = {email}").SingleAsync();
            }
            async Task Run()
            {
                using var scope = provider.CreateScope();
                await scheduler.RemindAbandonedCartsAsync(scope.ServiceProvider);
            }

            await Run();
            Assert.Equal(0, await Reminders()); // just touched: not abandoned

            // updatedAt is Prisma's own @updatedAt column, so backdating it takes raw SQL.
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE carts SET "updatedAt" = now() - interval '2 hours' WHERE "ownerKey" = {ownerKey}""");
            await Run();
            Assert.Equal(1, await Reminders());

            await Run();
            Assert.Equal(1, await Reminders()); // the same untouched cart is not reminded twice
        }
        finally
        {
            await db.Carts.Where(c => c.OwnerKey == ownerKey).ExecuteDeleteAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM outbox_events WHERE \"routingKey\" = 'cart.abandoned' AND payload->>'email' = {email}");
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }
    }
}
