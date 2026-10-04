using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace EcomApi.Http;

/// <summary>The JSON dialect of the API: camelCase, nulls omitted, and no HTML escaping (so "₹" and "&amp;" stay readable).</summary>
public static class Json
{
    /// <summary>For output and for reading data this app wrote itself (tolerant of properties it does not know).</summary>
    public static readonly JsonSerializerOptions Web = Create(strict: false);

    /// <summary>For request bodies: unknown properties, wrong types and nulls in non-nullable members are all refused.</summary>
    public static readonly JsonSerializerOptions Strict = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
            RespectNullableAnnotations = strict,
        };
        options.Converters.Add(new BsonDocumentConverter());
        options.Converters.Add(new BsonArrayConverter());
        return options;
    }

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Web);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Web);
}

internal static class BsonJson
{
    public static readonly JsonWriterSettings Settings = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };
}

/// <summary>Writes a MongoDB document as the plain JSON object it represents (the catalog's free-form attributes).</summary>
internal sealed class BsonDocumentConverter : JsonConverter<BsonDocument>
{
    public override BsonDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return BsonDocument.Parse(doc.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, BsonDocument value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToJson(BsonJson.Settings));
}

internal sealed class BsonArrayConverter : JsonConverter<BsonArray>
{
    public override BsonArray Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return BsonArray.Create(BsonSerializer.Deserialize<BsonDocument>("{\"a\":" + doc.RootElement.GetRawText() + "}")["a"]);
    }

    public override void Write(Utf8JsonWriter writer, BsonArray value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToJson(BsonJson.Settings));
}
