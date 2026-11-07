using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PgEmbed.Cli.Core;

/// <summary>
/// JSON for output, files and MCP results. Types resolve from <see cref="PgEmbedJsonContext"/>, generated at build
/// time, so nothing is serialized by reflection (which Native AOT cannot do): a type missing there fails fast.
/// </summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // output goes to terminals, files and tools, never into HTML: no " for quotes
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = PgEmbedJsonContext.Default
    };

    /// <summary>The same without indentation: for MCP results, where every character costs the assistant tokens.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(value, TypeInfo<T>(options ?? Options));

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>(Options));

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>(Compact));

    private static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
