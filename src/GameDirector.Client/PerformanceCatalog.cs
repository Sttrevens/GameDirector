using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

public sealed class PerformanceCatalog
{
    /// <summary>Stamp format 2 pins ManifestIdentity (manifest vocabulary). Format 1
    /// pinned manifest file bytes and is no longer accepted; run gd catalog-restamp.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public string ManifestSha256 { get; set; } = "";
    public List<PerformanceEntry> Performances { get; set; } = new();
}
public sealed class PerformanceEntry
{
    public string Actor { get; set; } = "";
    public string Clip { get; set; } = "";
    public string Meaning { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public List<string> UsefulFor { get; set; } = new();
    public List<string> Constraints { get; set; } = new();
    public List<PerformanceEvidence> Evidence { get; set; } = new();
}
public sealed class PerformanceEvidence
{
    // design: authored intent; observation: what a rendered take actually shows.
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Start { get; set; }
    public double? End { get; set; }
}
public static class CatalogTools
{
    public static PerformanceCatalog Scaffold(string manifestPath) {
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(manifestPath);
        return new PerformanceCatalog { ManifestSha256 = ManifestIdentity.Of(manifest),
            Performances = manifest.Actors.SelectMany(a => a.Clips.Select(c => new PerformanceEntry { Actor = a.Id, Clip = c })).ToList() };
    }
    public static IReadOnlyList<string> Validate(PerformanceCatalog catalog, CapabilityManifest manifest, string hash) {
        var errors = new List<string>();
        // Version 1 stamps are file-byte hashes and can never equal a vocabulary
        // identity; the useful message is the migration path, not a stale report.
        if (catalog.Version == 1) errors.Add("catalog version 1 is pinned to manifest file bytes; re-pin it with: gd catalog-restamp <catalog> --manifest <m.json>");
        else if (catalog.Version != PerformanceCatalog.CurrentVersion) errors.Add("unsupported catalog version");
        else if (catalog.ManifestSha256 != hash) errors.Add("catalog is stale: manifest changed; re-scout before reusing meanings");
        if (catalog.Performances == null) { errors.Add("performances required"); return errors; }
        var seen = new HashSet<(string,string)>();
        foreach (var p in catalog.Performances) {
            if (p == null) { errors.Add("null performance"); continue; }
            var key = p.Actor + "/" + p.Clip;
            if (!seen.Add((p.Actor,p.Clip))) errors.Add("duplicate performance: " + key);
            if (manifest.FindActor(p.Actor)?.Clips?.Contains(p.Clip) != true) errors.Add("unbound performance: " + key);
            if (p.DurationSeconds.HasValue && (!double.IsFinite(p.DurationSeconds.Value) || p.DurationSeconds <= 0)) errors.Add("invalid duration: " + key);
            if (p.Evidence == null || p.UsefulFor == null || p.Constraints == null) { errors.Add("null collections: " + key); continue; }
            bool hasClaims = !string.IsNullOrWhiteSpace(p.Meaning) || p.DurationSeconds.HasValue || p.UsefulFor.Count > 0 || p.Constraints.Count > 0;
            if (hasClaims && p.Evidence.Count == 0) errors.Add("meaning needs design or observed evidence: " + key);
            foreach (var e in p.Evidence) {
                if (e == null || (e.Kind != "design" && e.Kind != "observation") || string.IsNullOrWhiteSpace(e.Source)) { errors.Add("invalid evidence: " + key); continue; }
                if (e.Start.HasValue != e.End.HasValue || (e.Start.HasValue && (!double.IsFinite(e.Start.Value) || !double.IsFinite(e.End!.Value) || e.Start < 0 || e.End <= e.Start))) errors.Add("invalid evidence time range: " + key);
                if (e.Kind == "observation" && !e.Start.HasValue) errors.Add("observations need source timecodes: " + key);
            }
        }
        return errors;
    }
    public static object Inspect(string catalogPath, string manifestPath) {
        var catalog = DslJson.LoadAuthored<PerformanceCatalog>(catalogPath);
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(manifestPath);
        var errors = Validate(catalog, manifest, ManifestIdentity.Of(manifest));
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return new { status = "Valid", performances = catalog.Performances.Select(p => new {
            p.Actor, p.Clip, p.Meaning, p.DurationSeconds, p.UsefulFor, p.Constraints, p.Evidence,
            knowledge = p.Evidence.Any(e => e.Kind == "observation") ? "observed" : p.Evidence.Count > 0 ? "design-only; scout before shooting" : "unknown; scout before assigning meaning"
        }), note = "Evidence references are review inputs, not automatically verified claims. Asset names alone do not establish meaning." };
    }
    /// <summary>Migrates a catalog's stamp without touching curated entries. Allowed when
    /// the v1 file-byte stamp proves it vouched for exactly this manifest, when the current
    /// stamp already matches (no-op), or with force (an explicit human assertion).</summary>
    public static IReadOnlyList<string> Restamp(string catalogPath, string manifestPath, bool force) {
        // A catalog is a human-curated source file: edit the stored JSON tree, never
        // round-trip the model — the stamp changes; content, unknown fields, raw
        // non-ASCII text and line endings stay byte-identical.
        var text = File.ReadAllText(catalogPath);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidOperationException("catalog is not a JSON object");
        var catalog = DslJson.Deserialize<PerformanceCatalog>(text); // validation/decisions only; tolerant: curated unknown fields are preserved by design
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(manifestPath);
        var identity = ManifestIdentity.Of(manifest);
        var legacyProven = catalog.Version == 1 && catalog.ManifestSha256 == MediaTools.Hash(manifestPath);
        var current = catalog.Version == PerformanceCatalog.CurrentVersion && catalog.ManifestSha256 == identity;
        if (!force && !legacyProven && !current)
            throw new InvalidOperationException("catalog stamp does not match this manifest: the manifest changed, so meanings must be re-scouted, or re-run with --force to assert the pin explicitly");
        if (!current) {
            catalog.Version = PerformanceCatalog.CurrentVersion;
            catalog.ManifestSha256 = identity;
            node["version"] = PerformanceCatalog.CurrentVersion; // in-place: keeps position
            node["manifestSha256"] = identity;
            var crlf = text.Contains("\r\n");
            var output = JsonSerializer.Serialize(node, RestampWriteOptions).Replace("\r\n", "\n"); // WriteIndented follows Environment.NewLine; the file keeps its own style
            if (crlf) output = output.Replace("\n", "\r\n");
            if (text.EndsWith("\n")) output += crlf ? "\r\n" : "\n";
            var temp = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, output); File.Move(temp, catalogPath, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return Validate(catalog, manifest, identity);
    }
    // Raw UTF-8 on purpose: catalogs are curated source files, never embedded in HTML.
    private static readonly JsonSerializerOptions RestampWriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
