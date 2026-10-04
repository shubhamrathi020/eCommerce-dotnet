using System.ComponentModel.DataAnnotations;
using System.Text;
using EcomApi.Cache;
using EcomApi.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EcomApi.Http;

// Guards, as MVC filters, in the order a Nest request meets them: the blanket rate limit, the Redis-backed bucket limit,
// authentication, CSRF, then body validation, and only then the action. Resource filters run before model binding, so a
// refused request never has its body bound at all.

/// <summary>A stricter per-route rate limit than the blanket one (the endpoints worth brute-forcing).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictThrottleAttribute(int limit = 10) : Attribute
{
    public int Limit { get; } = limit;
}

/// <summary>Health probes are never rate limited.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipThrottleAttribute : Attribute;

/// <summary>In-memory fixed-window limiter per (route, client IP): the equivalent of @nestjs/throttler.</summary>
public sealed class ThrottleFilter(Throttler throttler, ApiConfig config) : IAsyncResourceFilter, IOrderedFilter
{
    public int Order => -3000;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (config.RateLimit && !context.ActionDescriptor.EndpointMetadata.OfType<SkipThrottleAttribute>().Any())
        {
            var strict = context.ActionDescriptor.EndpointMetadata.OfType<StrictThrottleAttribute>().FirstOrDefault();
            var handler = $"{context.HttpContext.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template}";
            var ip = context.HttpContext.ClientIp();
            if (!throttler.Allow($"{handler}|{ip}", strict?.Limit ?? config.RateLimits.Global.Limit, 60_000, out var retryAfter))
            {
                context.HttpContext.Response.Headers["Retry-After"] = retryAfter.ToString();
                throw new HttpError(429, "Too many requests. Please wait a moment and try again.");
            }
        }
        await next();
    }
}

/// <summary>One of the configured Redis-backed buckets (search, coupon, checkout).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RateBucketAttribute(string bucket) : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => -2000;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        await context.HttpContext.RequestServices.GetRequiredService<RateLimitGuard>().CheckAsync(bucket, context.HttpContext);
        await next();
    }
}

public static class RequestExtensions
{
    /// <summary>
    /// The client address, behind one trusted proxy hop (Docker, an ingress): the last X-Forwarded-For entry when present, else the socket's.
    /// </summary>
    public static string ClientIp(this HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (forwarded.Length > 0) return forwarded.Split(',')[^1].Trim();
        return ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
    }
}

public static class AuthContext
{
    private const string Key = "authUser";

    public static AuthUser? UserOf(this HttpContext ctx) => ctx.Items.TryGetValue(Key, out var u) ? u as AuthUser : null;

    public static AuthUser MustUser(this HttpContext ctx) => ctx.UserOf() ?? throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");

    internal static void Set(HttpContext ctx, AuthUser user) => ctx.Items[Key] = user;

    internal static string Bearer(HttpRequest req)
    {
        var header = req.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
    }
}

/// <summary>A valid bearer access token holding every listed permission. This is the real authorization boundary.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireAuthAttribute(params string[] permissions) : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => 0;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var token = AuthContext.Bearer(context.HttpContext.Request);
        if (token.Length == 0) throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");
        var user = context.HttpContext.RequestServices.GetRequiredService<JwtService>().Verify(token)
            ?? throw new AppError(ApiErrorCode.Unauthorized, "Your session has expired. Please sign in again.");
        AuthContext.Set(context.HttpContext, user);
        if (permissions.Any(p => !user.Permissions.Contains(p))) throw new AppError(ApiErrorCode.Forbidden, "You do not have permission to do that.");
        await next();
    }
}

/// <summary>For routes usable both signed in and as a guest: a valid token sets the caller, anything else leaves a guest.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class OptionalAuthAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => 0;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var token = AuthContext.Bearer(context.HttpContext.Request);
        if (token.Length > 0 && context.HttpContext.RequestServices.GetRequiredService<JwtService>().Verify(token) is { } user) AuthContext.Set(context.HttpContext, user);
        await next();
    }
}

/// <summary>
/// CSRF protection for the endpoints that act on a cookie. The cookie is SameSite=Lax, and these endpoints additionally require a
/// custom header: a cross-site page cannot send one without a CORS preflight, which only our own origins pass.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class CsrfAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => 10;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.HttpContext.Request.Headers["x-csrf"].ToString() != "1") throw new AppError(ApiErrorCode.Forbidden, "Missing CSRF header.");
        await next();
    }
}

/// <summary>
/// Body validation outcome: JSON that does not fit the declared shape (missing, mistyped, oversized or unknown properties) is one
/// generic 400 that never echoes the input back.
/// </summary>
public sealed class ValidateModelFilter : IActionFilter, IOrderedFilter
{
    public int Order => -1000;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid) throw new HttpError(400, Messages.NotValid);
        // An action that binds a body but received none (an empty JSON object bound to a type with required members) is already
        // invalid above; a missing optional body is fine.
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

/// <summary>IsIn: the value must be one of the listed strings.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OneOfAttribute(params string[] values) : ValidationAttribute
{
    public override bool IsValid(object? value) => value is null || (value is string s && values.Contains(s));
}

/// <summary>Writes a response the way Express would: JSON, text, or nothing, with an explicit status.</summary>
public sealed class ApiResult(int status, object? body, string? text = null, string? contentType = null) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var res = context.HttpContext.Response;
        res.StatusCode = status;
        if (text is not null)
        {
            res.ContentType = contentType ?? "text/html; charset=utf-8";
            await res.WriteAsync(text, Encoding.UTF8);
        }
        else if (body is not null)
        {
            res.ContentType = "application/json; charset=utf-8";
            await res.WriteAsync(Json.Serialize(body), Encoding.UTF8);
        }
    }
}
