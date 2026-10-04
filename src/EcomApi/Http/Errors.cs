namespace EcomApi.Http;

public enum ApiErrorCode
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Network,
    Unknown,
}

/// <summary>
/// The only error services throw on purpose. It carries the same code and fields the frontend's ApiException already
/// understands, so the HTTP adapter can rebuild it one-to-one.
/// </summary>
public sealed class AppError : Exception
{
    public ApiErrorCode Code { get; }
    public IReadOnlyDictionary<string, string>? Fields { get; }

    public AppError(ApiErrorCode code, string message, IReadOnlyDictionary<string, string>? fields = null) : base(message)
    {
        Code = code;
        Fields = fields;
    }

    public int Status => Code switch
    {
        ApiErrorCode.Validation => 400,
        ApiErrorCode.Unauthorized => 401,
        ApiErrorCode.Forbidden => 403,
        ApiErrorCode.NotFound => 404,
        ApiErrorCode.Conflict => 409,
        ApiErrorCode.Network => 503,
        _ => 500,
    };

    public static string CodeName(ApiErrorCode code) => code switch
    {
        ApiErrorCode.Validation => "validation",
        ApiErrorCode.Unauthorized => "unauthorized",
        ApiErrorCode.Forbidden => "forbidden",
        ApiErrorCode.NotFound => "not_found",
        ApiErrorCode.Conflict => "conflict",
        ApiErrorCode.Network => "network",
        _ => "unknown",
    };

    /// <summary>Throws a validation error when <paramref name="fields"/> has any entry (the "check the highlighted fields" pattern).</summary>
    public static void AssertNoFieldErrors(Dictionary<string, string> fields)
    {
        if (fields.Count > 0) throw new AppError(ApiErrorCode.Validation, "Please check the highlighted fields.", fields);
    }
}

/// <summary>A framework-level error with an HTTP status (unknown route, rate limit, malformed request).</summary>
public sealed class HttpError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static class Messages
{
    public const string NotValid = "The request was not valid.";
}
