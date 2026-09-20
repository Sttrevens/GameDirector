using GameDirector.Core.Dsl;

namespace GameDirector.Client;

public sealed class PerformanceCatalog
{
    public int Version { get; set; } = 1;
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
        var manifest = DslJson.Load<CapabilityManifest>(manifestPath);
        return new PerformanceCatalog { ManifestSha256 = MediaTools.Hash(manifestPath),
            Performances = manifest.Actors.SelectMany(a => a.Clips.Select(c => new PerformanceEntry { Actor = a.Id, Clip = c })).ToList() };
    }
    public static IReadOnlyList<string> Validate(PerformanceCatalog catalog, CapabilityManifest manifest, string hash) {
        var errors = new List<string>();
        if (catalog.Version != 1) errors.Add("unsupported catalog version");
        if (catalog.ManifestSha256 != hash) errors.Add("catalog is stale: manifest changed; re-scout before reusing meanings");
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
        var catalog = DslJson.Load<PerformanceCatalog>(catalogPath);
        var manifest = DslJson.Load<CapabilityManifest>(manifestPath);
        var errors = Validate(catalog, manifest, MediaTools.Hash(manifestPath));
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return new { status = "Valid", performances = catalog.Performances.Select(p => new {
            p.Actor, p.Clip, p.Meaning, p.DurationSeconds, p.UsefulFor, p.Constraints, p.Evidence,
            knowledge = p.Evidence.Any(e => e.Kind == "observation") ? "observed" : p.Evidence.Count > 0 ? "design-only; scout before shooting" : "unknown; scout before assigning meaning"
        }), note = "Evidence references are review inputs, not automatically verified claims. Asset names alone do not establish meaning." };
    }
}
