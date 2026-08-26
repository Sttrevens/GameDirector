using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDirector.Client
{
    /// <summary>
    /// Shared JSON configuration for DSL assets and bridge payloads.
    /// camelCase on the wire; nulls omitted; tolerant reads.
    /// </summary>
    public static class DslJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static T Deserialize<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException("deserialized to null: " + typeof(T).Name);

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T Load<T>(string path) => Deserialize<T>(File.ReadAllText(path));

        public static void Save<T>(string path, T value) => File.WriteAllText(path, Serialize(value));
    }
}
