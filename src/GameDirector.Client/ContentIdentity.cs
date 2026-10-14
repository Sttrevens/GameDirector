using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

/// <summary>Semantic content identity for in-memory values: caches, idempotency
/// and decision evidence. Canonical form is compact JSON (no indentation, no
/// newlines) with object properties sorted recursively by ordinal key — JSON
/// objects are unordered by spec, so insertion order must not change identity —
/// arrays keep order, and tokens are emitted as System.Text.Json writes them.
/// SHA-256 over UTF-8, lowercase hex.</summary>
public static class ContentIdentity
{
    /// <summary>Format 1 was indented, unsorted and platform-newline dependent;
    /// identity values changed with format 2. Stored values hashed by format 1
    /// must be recomputed or read through a versioned legacy path instead.</summary>
    public const int Format = 2;

    public static string Of<T>(T value) => OfNode(JsonSerializer.SerializeToNode(value, DslJson.Options));

    public static string OfNode(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, node);
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValue value:
                value.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Vocabulary identity of a manifest: what a performance catalog vouches
/// for and what decision evidence references. Session/machine-volatile values
/// and public read-only inspection surfaces (CapabilityKeys.NonIdentity) are excluded, because they do
/// not change what the game can perform. The full ContentIdentity, fingerprint
/// included, remains the take-cache key — pixels depend on the live source.</summary>
public static class ManifestIdentity
{
    public static string Of(CapabilityManifest manifest)
    {
        var copy = DslJson.Deserialize<CapabilityManifest>(DslJson.Serialize(manifest)); // never mutate the input
        if (copy.Capabilities != null) foreach (var key in CapabilityKeys.NonIdentity) copy.Capabilities.Remove(key);
        return ContentIdentity.Of(copy);
    }
}
