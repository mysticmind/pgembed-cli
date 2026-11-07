using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PgEmbed.Cli.Core;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // output goes to terminals, files and tools, never into HTML: no \u0022 for quotes
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>The same without indentation: for MCP results, where every character costs the assistant tokens.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
