using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameDirector.Core.Dsl;
using GameDirector.Core.Capture;

namespace GameDirector.Client
{
    public enum AuthoredDocumentKind { Unspecified, Timeline, Manifest }

    /// <summary>
    /// Raised when a human/LLM-authored DSL file fails strict parsing. The message
    /// names the JSON path of the offending field in the document the author wrote
    /// ("cues[3].shot.durationSecond: unknown property", "version: required"), so a
    /// mistake fails at the field instead of silently taking a CLR default.
    /// A JsonException so existing JSON error handling (repair loops, CLI/MCP
    /// catch-alls) treats it like any other malformed-input failure.
    /// </summary>
    public sealed class AuthoredInputException : JsonException
    {
        public AuthoredInputException(string message, AuthoredDocumentKind document = AuthoredDocumentKind.Unspecified) : base(message) { Document = document; }
        /// <summary>Which document failed strict parsing when a caller loads
        /// more than one. The shared kind cannot drift between tagger and reader;
        /// callers never string-match file paths out of the message.</summary>
        public AuthoredDocumentKind Document { get; }
    }

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

        /// <summary>
        /// Strict twin of <see cref="Options"/> for files a human or LLM wrote:
        /// an unknown property at any depth is an error. Bridge payloads, stored
        /// receipts/journals and Workbench store records keep the tolerant
        /// defaults above, so client/package version skew never breaks takes.
        /// </summary>
        public static readonly JsonSerializerOptions AuthoredOptions = new JsonSerializerOptions(Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        // Required top-level fields per authored document type, checked on the JSON
        // tree BEFORE deserialization: a missing field must name itself ("version:
        // required") instead of silently becoming the CLR default ("0.1").
        private static readonly Dictionary<Type, string[]> RequiredTopLevel = new Dictionary<Type, string[]>
        {
            [typeof(TimelineAsset)] = new[] { "version", "id", "cues" },
            [typeof(EditPlan)] = new[] { "version" },
            [typeof(FilmPlan)] = new[] { "version" },
            [typeof(ExpectFile)] = new[] { "version", "timeline", "authoredSha256", "fps", "beats", "sizes" },
            [typeof(PerformanceCatalog)] = new[] { "version", "manifestSha256", "performances" },
            [typeof(CapabilityManifest)] = new[] { "manifestVersion" }
        };

        public static T Deserialize<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException("deserialized to null: " + typeof(T).Name);

        /// <summary>Strict load of an authored document: required top-level fields
        /// first, then <see cref="AuthoredOptions"/>. Throws <see cref="AuthoredInputException"/>.</summary>
        public static T DeserializeAuthored<T>(string json)
        {
            RequireTopLevel<T>(json);
            try
            {
                return JsonSerializer.Deserialize<T>(json, AuthoredOptions)
                    ?? throw new AuthoredInputException("document: deserialized to null");
            }
            catch (JsonException ex) when (!(ex is AuthoredInputException))
            {
                throw new AuthoredInputException(Describe(ex));
            }
        }

        /// <summary>Strict load of an authored file; the message names file and field.</summary>
        public static T LoadAuthored<T>(string path)
        {
            try { return DeserializeAuthored<T>(File.ReadAllText(path)); }
            catch (AuthoredInputException ex) { throw new AuthoredInputException(path + ": " + ex.Message); }
        }

        private static void RequireTopLevel<T>(string json)
        {
            if (!RequiredTopLevel.TryGetValue(typeof(T), out var fields)) return;
            JsonElement root;
            try
            {
                using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                    root = doc.RootElement.Clone();
            }
            catch (JsonException ex) { throw new AuthoredInputException(Describe(ex)); }
            if (root.ValueKind != JsonValueKind.Object)
                throw new AuthoredInputException("document: expected a JSON object");
            foreach (var field in fields)
            {
                var found = false;
                foreach (var property in root.EnumerateObject())
                    if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found) throw new AuthoredInputException(field + ": required");
            }
        }

        // JsonException messages carry a "Path: $.x | LineNumber..." suffix; the
        // authored-input contract is one line reading "cues[3].shot.durationSecond: unknown property".
        private static string Describe(JsonException ex)
        {
            var message = ex.Message;
            var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
            if (cut >= 0) message = message.Substring(0, cut);
            if (message.Contains("could not be mapped to any .NET member")) message = "unknown property";
            var path = ex.Path;
            if (path != null && path.StartsWith("$.", StringComparison.Ordinal)) path = path.Substring(2);
            return string.IsNullOrEmpty(path) || path == "$" ? message : path + ": " + message;
        }

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T Load<T>(string path) => Deserialize<T>(File.ReadAllText(path));

        public static void SaveAtomic<T>(string path,T value) {
            var temp=path+"."+System.Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllText(temp,Serialize(value));File.Move(temp,path,true);}
            finally {if(File.Exists(temp))File.Delete(temp);}
        }

        public static void Save<T>(string path, T value) => File.WriteAllText(path, Serialize(value));
    }
}
