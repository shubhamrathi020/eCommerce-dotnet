using System.Text;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace EcomApi.Http;

/// <summary>
/// The API documentation page, served exactly where the Nest API serves it: /docs (Swagger UI), /docs/openapi.json and the UI's own assets.
/// The pages, the initialiser and the document in docs/ are the files eCommerce-api exports (test/export-openapi.spec.ts); the UI's scripts
/// and styles come from the Swashbuckle.AspNetCore.SwaggerUI package.
/// </summary>
public static class Docs
{
    public static IApplicationBuilder UseDocs(this IApplicationBuilder app)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "docs");
        var index = File.ReadAllText(Path.Combine(dir, "index.html"));
        var indexSlash = File.ReadAllText(Path.Combine(dir, "index-slash.html"));
        var init = File.ReadAllText(Path.Combine(dir, "swagger-ui-init.js"));
        var document = File.ReadAllText(Path.Combine(dir, "openapi.json"));

        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                var path = ctx.Request.Path.Value ?? "";
                (string Body, string Type)? page = path.ToLowerInvariant() switch
                {
                    "/docs" => (index, "text/html; charset=utf-8"),
                    // Nest serves two slightly different pages (the one under /docs/ links its assets relative to that folder).
                    "/docs/" => (indexSlash, "text/html; charset=utf-8"),
                    "/docs/swagger-ui-init.js" => (init, "application/javascript; charset=utf-8"),
                    "/docs/openapi.json" => (document, "application/json; charset=utf-8"),
                    _ => null,
                };
                if (page is { } found)
                {
                    ctx.Response.ContentType = found.Type;
                    await ctx.Response.WriteAsync(found.Body, Encoding.UTF8);
                    return;
                }
            }
            await next();
        });
        return app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "docs";
            options.SwaggerEndpoint("/docs/openapi.json", "eCommerce API");
        });
    }
}
