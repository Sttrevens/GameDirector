using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameDirector.Client;

// A parser/shape rejection of persisted bytes is a storage diagnosis, NOT a
// caller-input error. Preserve the original diagnostic and the original files.
// Actual filesystem I/O/permission failures are not presumed to mean corruption.
public static class StoredJson
{
    public static T Load<T>(string path) => Diagnose(() => DslJson.Load<T>(path));
    public static JsonNode? Node(string path) => Diagnose(() => JsonNode.Parse(File.ReadAllText(path)));
    public static T Diagnose<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FileNotFoundException or ArgumentException)
        { throw new StoreIntegrityException(ex.Message, ex); }
    }
}
