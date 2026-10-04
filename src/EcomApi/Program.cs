using System.Net.Http;
using EcomApi.Cache;
using EcomApi.Config;
using EcomApi.Controllers;
using EcomApi.Data;
using EcomApi.Http;
using EcomApi.Metrics;
using EcomApi.Services;
using EcomApi.Services.Catalog;
using EcomApi.Services.Commerce;
using EcomApi.Services.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// `EcomApi -healthcheck` probes the running server (used by container health checks that have no curl or wget).
if (args.Contains("-healthcheck"))
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var response = await http.GetAsync($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PORT") ?? "3333"}/healthz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

ApiConfig config;
try
{
    // Fails fast with a readable list when required settings are missing (never starts half-configured).
    config = ApiConfig.FromEnvironment();
}
catch (InvalidOperationException error)
{
    Log.Error("Bootstrap", error.Message);
    return 1;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = config.Production ? "Production" : "Development" });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");

var services = builder.Services;
services.AddSingleton(config);
services.AddSingleton(_ => EcomDb.CreateDataSource(config.DatabaseUrl));
services.AddDbContext<EcomDb>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.MapEnum<RoleKind>("Role", nameTranslator: EcomDb.NoTranslation)));
services.AddSingleton<RedisService>();
services.AddSingleton<CacheService>();
services.AddSingleton<CouponRedemptionService>();
services.AddSingleton<RateLimitGuard>();
services.AddSingleton<Throttler>();
services.AddSingleton<MongoService>();
services.AddSingleton<SearchService>();
services.AddSingleton<MailService>();
services.AddSingleton<PasswordService>();
services.AddSingleton<JwtService>();
services.AddSingleton<RabbitService>();
services.AddSingleton<RazorpayService>();
services.AddSingleton<OrderEventsService>();
services.AddSingleton<NotificationConsumerService>();
services.AddSingleton<MetricsService>();
services.AddScoped<TokenService>();
services.AddScoped<AuthService>();
services.AddScoped<AccountsService>();
services.AddScoped<AddressesService>();
services.AddScoped<CatalogService>();
services.AddScoped<AdminCatalogService>();
services.AddScoped<CartService>();
services.AddScoped<InventoryService>();
services.AddScoped<OutboxService>();
services.AddScoped<OrderService>();
services.AddScoped<PaymentService>();
services.AddScoped<AdminOrderService>();
services.AddHostedService<OutboxRelayService>();
services.AddHostedService<SchedulerService>();
services.AddSingleton<SchedulerService>(sp => sp.GetServices<IHostedService>().OfType<SchedulerService>().First());

services.AddControllers(options =>
{
    options.Filters.Add<ThrottleFilter>();
    options.Filters.Add<ValidateModelFilter>();
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
})
.ConfigureApiBehaviorOptions(options => options.SuppressModelStateInvalidFilter = true)
.AddJsonOptions(options =>
{
    var strict = Json.Strict;
    var target = options.JsonSerializerOptions;
    target.PropertyNamingPolicy = strict.PropertyNamingPolicy;
    target.PropertyNameCaseInsensitive = false;
    target.DefaultIgnoreCondition = strict.DefaultIgnoreCondition;
    target.Encoder = strict.Encoder;
    target.NumberHandling = strict.NumberHandling;
    target.UnmappedMemberHandling = strict.UnmappedMemberHandling;
    target.RespectNullableAnnotations = true;
});

var app = builder.Build();
var metrics = app.Services.GetRequiredService<MetricsService>();

app.UseExpressSemantics();
app.UseSecurityAndCors(config);
app.UseEarlyErrors();
app.UseBodyParser();
app.UseRequestContext(metrics);
app.UseRouting();
app.MapControllers();

var redis = app.Services.GetRequiredService<RedisService>();
var mongo = app.Services.GetRequiredService<MongoService>();
var rabbit = app.Services.GetRequiredService<RabbitService>();
try
{
    // Connections first, then the jobs that use them (the broker topology must exist before anything publishes into it).
    await Task.WhenAll(redis.InitAsync(), mongo.InitAsync(), rabbit.InitAsync());
    await app.Services.GetRequiredService<NotificationConsumerService>().StartAsync();
}
catch (Exception error)
{
    Log.Error("Bootstrap", $"could not connect to a dependency: {error.Message}");
    return 1;
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<Throttler>().Dispose();
    rabbit.CloseAsync().GetAwaiter().GetResult();
    mongo.CloseAsync().GetAwaiter().GetResult();
    redis.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

Log.Info("Bootstrap", $"API listening on port {config.Port}");
await app.RunAsync();
return 0;

/// <summary>Lets the test project reference the entry point.</summary>
public partial class Program;
