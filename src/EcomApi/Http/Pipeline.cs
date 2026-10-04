using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EcomApi.Config;
using EcomApi.Metrics;
using Microsoft.AspNetCore.Http.Features;

namespace EcomApi.Http;

// The middleware that makes this ASP.NET Core server answer like the Express-based NestJS one: the same security headers,
// CORS rules, body handling, correlation ids, error bodies and conditional-GET behaviour.

public static class Pipeline
{
    public const string RawBodyKey = "rawBody";
    public const string ErrorWrittenKey = "errorWritten";
    public const string OriginalBodyKey = "originalBody";

    /// <summary>Express answers HEAD from the matching GET handler and adds a weak ETag to every body; this does both.</summary>
    public static IApplicationBuilder UseExpressSemantics(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var head = HttpMethods.IsHead(ctx.Request.Method);
        if (head) ctx.Request.Method = HttpMethods.Get;
        var original = ctx.Response.Body;
        // Server-sent events must reach the client as they are written, not when the response ends: that action takes the real stream.
        ctx.Items[OriginalBodyKey] = original;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next();
        }
        finally
        {
            ctx.Response.Body = original;
        }
        if (ctx.Response.HasStarted) return;
        var bytes = buffer.ToArray();
        var headers = ctx.Response.Headers;
        if (bytes.Length > 0 && !headers.ContainsKey("ETag")) headers.ETag = WeakEtag(bytes);
        var status = ctx.Response.StatusCode;
        if (bytes.Length > 0 && ctx.Request.Method == HttpMethods.Get && status is >= 200 and < 300 && IsFresh(ctx.Request, headers.ETag.ToString()))
        {
            ctx.Response.StatusCode = 304;
            headers.Remove("Content-Type");
            headers.Remove("Content-Length");
            return;
        }
        if (status is 204 or 304) return;
        headers.ContentLength = bytes.Length;
        if (!head && bytes.Length > 0) await original.WriteAsync(bytes);
    });

    private static string WeakEtag(byte[] body)
    {
        var hash = Convert.ToBase64String(SHA1.HashData(body))[..27];
        return $"W/\"{body.Length:x}-{hash}\"";
    }

    private static bool IsFresh(HttpRequest req, string etag)
    {
        var noneMatch = req.Headers.IfNoneMatch.ToString();
        if (noneMatch.Length == 0 || etag.Length == 0) return false;
        if (req.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase)) return false;
        if (noneMatch.Trim() == "*") return true;
        static string Strip(string s) => s.Trim().StartsWith("W/", StringComparison.Ordinal) ? s.Trim()[2..] : s.Trim();
        var current = Strip(etag);
        return noneMatch.Split(',').Any(candidate => Strip(candidate) == current);
    }

    private static readonly (string, string)[] SecurityHeaders =
    [
        ("Content-Security-Policy", "default-src 'none';base-uri 'self';font-src 'self' https: data:;form-action 'self';frame-ancestors 'none';img-src 'self' data:;object-src 'none';script-src 'self';script-src-attr 'none';style-src 'self' https: 'unsafe-inline';upgrade-insecure-requests"),
        ("Cross-Origin-Opener-Policy", "same-origin"),
        ("Cross-Origin-Resource-Policy", "same-origin"),
        ("Origin-Agent-Cluster", "?1"),
        ("Referrer-Policy", "no-referrer"),
        ("Strict-Transport-Security", "max-age=31536000; includeSubDomains"),
        ("X-Content-Type-Options", "nosniff"),
        ("X-DNS-Prefetch-Control", "off"),
        ("X-Download-Options", "noopen"),
        ("X-Frame-Options", "SAMEORIGIN"),
        ("X-Permitted-Cross-Domain-Policies", "none"),
        ("X-XSS-Protection", "0"),
    ];

    private const string AllowedHeaders = "content-type,authorization,x-csrf,x-request-id";

    /// <summary>Security headers (a JSON API needs no content policy beyond "nothing") and CORS for the listed origins only.</summary>
    public static IApplicationBuilder UseSecurityAndCors(this IApplicationBuilder app, ApiConfig config) => app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        foreach (var (name, value) in SecurityHeaders) headers[name] = value;
        // Credentials only for the listed origins. Anything else gets no CORS headers at all (but the preflight still answers).
        headers.Append("Vary", "Origin");
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && config.CorsOrigins.Contains(origin)) headers["Access-Control-Allow-Origin"] = origin;
        headers["Access-Control-Allow-Credentials"] = "true";
        if (HttpMethods.IsOptions(ctx.Request.Method))
        {
            headers["Access-Control-Allow-Methods"] = "GET,HEAD,PUT,PATCH,POST,DELETE";
            headers["Access-Control-Allow-Headers"] = AllowedHeaders;
            headers["Access-Control-Max-Age"] = "600";
            headers["Access-Control-Expose-Headers"] = "x-request-id";
            ctx.Response.StatusCode = 204;
            headers.ContentLength = 0;
            return;
        }
        headers["Access-Control-Expose-Headers"] = "x-request-id";
        await next();
    });

    /// <summary>
    /// Mirrors express.json({ limit: '100kb' }): a JSON object or array body is parsed (malformed JSON is a 400 with the parser's
    /// message), any other content type leaves an empty body, and the exact raw text is kept for the Razorpay webhook signature.
    /// </summary>
    public static IApplicationBuilder UseBodyParser(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var req = ctx.Request;
        var method = req.Method;
        var hasBody = (req.ContentLength ?? 0) > 0 || req.Headers.TransferEncoding.Count > 0;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsDelete(method) && !hasBody || !hasBody)
        {
            await next();
            return;
        }
        var contentType = req.ContentType ?? "";
        var isJson = Regex.IsMatch(contentType, @"^application/([a-z0-9.+-]*\+)?json\b", RegexOptions.IgnoreCase);
        if (!isJson)
        {
            // Express leaves req.body empty for content types it does not parse; the DTO checks then refuse it as incomplete.
            req.Body = new MemoryStream("{}"u8.ToArray());
            req.ContentType = "application/json";
            req.ContentLength = 2;
            await next();
            return;
        }
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await req.Body.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, read);
            // A too-large body is not mapped to a friendly status by the Nest API either: it surfaces as a plain 500.
            if (ms.Length > 100 * 1024) throw new InvalidOperationException("request entity too large");
        }
        var bytes = ms.ToArray();
        if (bytes.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    throw new HttpError(400, $"Unexpected token '{Encoding.UTF8.GetString(bytes).TrimStart()[..1]}', \"{Encoding.UTF8.GetString(bytes)}\" is not valid JSON");
            }
            catch (JsonException e)
            {
                throw new HttpError(400, e.Message);
            }
        }
        ctx.Items[RawBodyKey] = Encoding.UTF8.GetString(bytes);
        req.Body = new MemoryStream(bytes.Length == 0 ? "{}"u8.ToArray() : bytes);
        await next();
    });

    private static readonly Regex SafeId = new("^[A-Za-z0-9._-]{8,64}$", RegexOptions.Compiled);

    /// <summary>
    /// Correlation id, structured access log and request metrics; also renders every error the rest of the pipeline raises,
    /// so the access log and the metrics see the real status.
    /// </summary>
    public static IApplicationBuilder UseRequestContext(this IApplicationBuilder app, MetricsService metrics) => app.Use(async (ctx, next) =>
    {
        // A caller-supplied id is reused only when it looks like an id (so a client cannot inject text into our logs).
        var incoming = ctx.Request.Headers["x-request-id"].ToString();
        var id = SafeId.IsMatch(incoming) ? incoming : Guid.NewGuid().ToString();
        ctx.Request.Headers["x-request-id"] = id;
        ctx.Response.Headers["x-request-id"] = id;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next();
            // An unknown route (or a known path with the wrong method) is a plain "not found" in the API's error shape.
            if (ctx.Response.StatusCode is 404 or 405 && !ctx.Response.HasStarted && !ctx.Items.ContainsKey(ErrorWrittenKey))
                await ErrorWriter.WriteAsync(ctx, new HttpError(404, "Not found"));
        }
        catch (Exception error) when (!ctx.Response.HasStarted)
        {
            await ErrorWriter.WriteAsync(ctx, error);
        }
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var path = ctx.Request.Path.Value ?? "/";
        Log.Write("log", "HTTP", JsonSerializer.Serialize(new { requestId = id, method = ctx.Request.Method, path, status = ctx.Response.StatusCode, ms = Math.Round(ms * 10) / 10 }, Json.Web));
        if (path != "/metrics") metrics.RecordHttpRequest(ctx.Request.Method, RouteLabel(ctx), ctx.Response.StatusCode, ms / 1000);
    });

    /// <summary>The matched path pattern (/orders/:id), never the raw URL: one time series per order id would grow forever.</summary>
    private static string RouteLabel(HttpContext ctx)
    {
        var pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (pattern is null) return "unmatched";
        return "/" + Regex.Replace(pattern.TrimStart('/'), @"\{([^}:?*]+)[^}]*\}", ":$1");
    }

    /// <summary>Renders failures raised before the request context exists (an unreadable body).</summary>
    public static IApplicationBuilder UseEarlyErrors(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception error) when (!ctx.Response.HasStarted)
        {
            await ErrorWriter.WriteAsync(ctx, error);
        }
    });
}

/// <summary>Turns every error into the ApiError JSON shape the frontend already handles.</summary>
public static class ErrorWriter
{
    /// <summary>The message with every inner exception's message, so a wrapped database error is readable in the log.</summary>
    private static string Describe(Exception error)
    {
        var parts = new List<string>();
        for (var e = error; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" -> ", parts);
    }

    public static async Task WriteAsync(HttpContext ctx, Exception error)
    {
        var requestId = ctx.Request.Headers["x-request-id"].ToString();
        int status;
        string code;
        string message;
        IReadOnlyDictionary<string, string>? fields = null;
        switch (error)
        {
            case AppError app:
                status = app.Status;
                code = AppError.CodeName(app.Code);
                message = app.Message;
                fields = app.Fields;
                break;
            case HttpError http:
                status = http.Status;
                code = http.Status switch { 400 or 422 => "validation", 401 => "unauthorized", 403 or 429 => "forbidden", 404 => "not_found", 409 => "conflict", >= 500 => "unknown", _ => "validation" };
                message = http.Status == 429 ? "Too many requests. Please wait a moment and try again." : http.Status == 404 ? "Not found" : http.Message;
                break;
            case BadHttpRequestException bad:
                status = 400;
                code = "validation";
                message = bad.Message;
                break;
            default:
                Log.Write("error", "ApiError", JsonSerializer.Serialize(new { requestId, method = ctx.Request.Method, path = ctx.Request.Path.Value, error = new { name = error.GetType().Name, message = Describe(error), stack = error.StackTrace } }, Json.Web));
                status = 500;
                code = "unknown";
                message = "Something went wrong. Please try again.";
                break;
        }
        ctx.Items[Pipeline.ErrorWrittenKey] = true;
        // Whatever a failing handler had already written is dropped; headers set so far (cookies, security headers) stay, as in Express.
        if (ctx.Response.Body.CanSeek) ctx.Response.Body.SetLength(0);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var body = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (fields is not null) body["fields"] = fields;
        if (requestId.Length > 0) body["requestId"] = requestId;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json.Web), Encoding.UTF8);
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string? LogFile = Environment.GetEnvironmentVariable("ECOM_LOG_FILE");

    /// <summary>One JSON object per line: stdout for normal lines, stderr for errors.</summary>
    public static void Write(string level, string logger, string message)
    {
        var line = JsonSerializer.Serialize(new { time = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), level, logger, message }, Json.Web);
        lock (Gate)
        {
            (level == "error" ? Console.Error : Console.Out).WriteLine(line);
            // Handy when a test harness swallows the server's output: ECOM_LOG_FILE=<path> also appends every line to a file.
            if (LogFile is not null) File.AppendAllText(LogFile, line + Environment.NewLine);
        }
    }

    public static void Info(string logger, string message) => Write("log", logger, message);
    public static void Warn(string logger, string message) => Write("warn", logger, message);
    public static void Error(string logger, string message) => Write("error", logger, message);
}
