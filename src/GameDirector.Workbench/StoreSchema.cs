using System.Reflection;
using System.Text.Json;
using GameDirector.Client;

namespace GameDirector.Workbench;

/// <summary>
/// store.json — the store's only version seam (store schema guard). Refusal checks are
/// read-only and run FIRST (Check, before .owner and before any load); the single
/// write (Stamp) happens only after the process owns the store and the load
/// succeeded, so an older binary can never open a newer store and silently drop
/// fields by rewriting whole objects. All future shrink-wrapped changes migrate
/// here, in version order, at this single entrance.
/// </summary>
public static class StoreSchema
{
    /// <summary>Schema this Workbench writes: 2 = FilmDocument v2 + catalog v2.</summary>
    public const int CurrentSchema = 2;
    /// <summary>Oldest reader schema a store written by this build still allows.</summary>
    public const int MinReaderSchema = 2;
    public const string FileName = "store.json";

    /// <summary>The persisted stamp. No defaults: a stamp missing fields reads as
    /// corrupt instead of silently meaning "current".</summary>
    public sealed class State
    {
        public int SchemaVersion { get; set; }
        public int MinReaderSchema { get; set; }
        public string WriterProductVersion { get; set; } = "";
    }

    /// <summary>The Workbench assembly's informational version (Directory.Build.props
    /// Version flows into it), stamped so a rejected store names its writer.</summary>
    public static string ProductVersion
    {
        get
        {
            var info = typeof(StoreSchema).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var value = info?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(value)) return "0.0.0-local";
            var plus = value.IndexOf('+'); // strip SourceLink build metadata
            return plus > 0 ? value[..plus] : value;
        }
    }

    /// <summary>The stamp this build writes, stated explicitly — never derived from
    /// the tolerant reader defaults.</summary>
    private static State Current() => new()
    {
        SchemaVersion = CurrentSchema,
        MinReaderSchema = MinReaderSchema,
        WriterProductVersion = ProductVersion,
    };

    /// <summary>Read-only validation, called before .owner and before any load:
    /// refusal never writes. Returns the prior state when store.json exists, else
    /// null. Throws — creating and changing nothing — when the stamp is corrupt
    /// (unparseable, non-positive versions, minReader above schema, unnamed writer)
    /// or requires a newer Workbench.</summary>
    public static State? Check(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return null;
        State state;
        try { state = DslJson.Load<State>(path); }
        catch (Exception ex) when (ex is JsonException or UnauthorizedAccessException)
        {
            throw Corrupt(path, ex);
        }
        if (state.SchemaVersion <= 0 || state.MinReaderSchema <= 0 ||
            state.MinReaderSchema > state.SchemaVersion || string.IsNullOrWhiteSpace(state.WriterProductVersion))
            throw Corrupt(path);
        if (state.MinReaderSchema > CurrentSchema)
            throw new StoreIntegrityFault("This store was written by GameDirector " + state.WriterProductVersion +
                ", which requires a newer Workbench; update GameDirector or choose another --data folder.");
        return state;
    }

    /// <summary>The only write, and only once the process owns the store (.owner
    /// held) and the load succeeded: stamp a new or pre-guard store, or advance an
    /// older writer's stamp. schemaVersion never decreases; a newer writer's stamp
    /// is left untouched.</summary>
    public static void Stamp(string root, State? prior)
    {
        if (prior != null && prior.SchemaVersion >= CurrentSchema) return;
        DslJson.SaveAtomic(Path.Combine(root, FileName), Current());
    }

    private static StoreIntegrityFault Corrupt(string path, Exception? inner = null) =>
        new("Workbench store schema file is corrupt and was left untouched: " + path, inner);
}
