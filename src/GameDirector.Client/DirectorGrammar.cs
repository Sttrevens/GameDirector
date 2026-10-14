using System.Globalization;
using System.Reflection;
using System.Text;
using GameDirector.Core.Compilation;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

/// <summary>
/// The DSL quick reference an LLM reads before authoring. GENERATED from the
/// code that enforces it — never restate it: cue types from CueTypes.All,
/// shot/frame/ease/param vocabulary from ShotVocabulary, omitted-field defaults
/// from the POCO initializers, capability policy and rules from CapabilitySpecs,
/// diagnostic codes from the TimelineCompiler constants, workflow names from
/// McpToolNames. A Core addition the generator cannot place throws here, and
/// GrammarContractTests pins the coverage — the text cannot drift silently.
/// </summary>
public static class DirectorGrammar
{
    // Per-cue one-line signature and note, keyed by the CueTypes constants.
    // The camera.shot payload is reflected from ShotSpec, not written here.
    private static readonly Dictionary<string, (string Fields, string Note)> CueLines = new()
    {
        [CueTypes.CameraShot] = ("t, aspect?:{min?,max?}, shot:{ " + ShotFields() + " }",  "start a camera shot; durationSeconds=0 holds until the next shot cue"),
        [CueTypes.ActorSpawn] = ("t, role, location, headingTo?", "bring a role into the scene at a manifest location"),
        [CueTypes.ActorDespawn] = ("t, role", "remove a role from the scene"),
        [CueTypes.ActorAnim] = ("t, role, clip, fade?", "crossfade the role's actor to a declared clip"),
        [CueTypes.ActorMove] = ("t, role, to, speed?, headingTo?", "move a role toward a location (speed m/s)"),
        [CueTypes.ActorFace] = ("t, role, headingTo", "turn a role toward a role id or location id"),
        [CueTypes.AudioPlay] = ("t, audioId, volume?", "play a manifest audio id; volume 0..1"),
        [CueTypes.AudioStop] = ("t, audioId", "stop a manifest audio id"),
        [CueTypes.WorldTimeScale] = ("t, scale, duration?", "duration>0 auto-restores to 1.0 via an injected cue"),
        [CueTypes.Marker] = ("t, label", "edit beat / review annotation"),
    };

    public static string Build()
    {
        var shot = new ShotSpec();
        var cue = new Cue();
        var sb = new StringBuilder();
        sb.AppendLine("GameDirector DSL " + TimelineCompiler.SupportedVersion + " — generated from Core by gd grammar / " + McpToolNames.Grammar + "; the compiler enforces every rule below. Validate before play.");
        sb.AppendLine();
        sb.AppendLine("Cue types (JSON, times in seconds):");
        foreach (var type in CueTypes.All)
        {
            if (!CueLines.TryGetValue(type, out var line))
                throw new InvalidOperationException("DirectorGrammar has no line for cue type '" + type + "'; add one next to CueTypes.");
            sb.AppendLine("  " + type.PadRight(17) + "{ " + line.Fields + " }   " + line.Note);
        }
        sb.AppendLine();
        sb.AppendLine("Shot vocabulary (the manifest may narrow it; capabilities below narrow it further):");
        sb.AppendLine("  shot.type   " + string.Join(" | ", ShotVocabulary.Shots.Select(s => s.Id + (s.RequiresTarget ? " (to required)" : " (to optional)"))));
        sb.AppendLine("  shot.frame  " + string.Join(" · ", ShotVocabulary.Frames.Select(f => f.Id + " " + Num(f.Coverage))) + "   (coverage = subject-height fraction for auto-framing; explicit anchors keep their authored composition)");
        sb.AppendLine("  shot.ease   " + string.Join(" | ", ShotVocabulary.EaseSpecs.Select(e => e.Id + " (" + e.Expression + ")")));
        sb.AppendLine("  lens        fov " + Num(ShotVocabulary.MinFov) + ".." + Num(ShotVocabulary.MaxFov) + " (default " + Num(ShotVocabulary.DefaultFov) + ") or focalLengthMm (" + Num(ShotVocabulary.SensorSizeMm) + " mm sensor height); never both");
        sb.AppendLine("  anchors     from/to are manifest location ids or \"" + ShotVocabulary.CurrentAnchor + "\" (wherever the camera is now)");
        sb.AppendLine("  aspect      basic Core capability, not a camera param: camera.shot only; [min,max) on width/height; omitted min=0, max=infinity");
        sb.AppendLine("              same-t alternates must be contiguous, partition (0,infinity), share durationSeconds and event role/label, and have no unhinted twin (" + TimelineCompiler.EAspectHint + ")");
        sb.AppendLine("              every alternate is validated; exactly one is kept, aspect cleared. gd validate/compile (including --take) require explicit --width and --height with hints; " + McpToolNames.ValidateTimeline + " requires both width and height");
        sb.AppendLine("              gd play / " + McpToolNames.PlayTimeline + " refuse hints client-side (no size); use gd take. Engines receive resolved cues only");
        sb.AppendLine("              after a hinted group, from/to current requires a strictly earlier (beyond cue epsilon) unhinted explicit-anchor cue whose subject/lookAt survives that instant (" + TimelineCompiler.EAspectHint + ")");
        sb.AppendLine("  shot.with   distinct known role ids, never the subject; review-only until contain ships; may enter inside the shot window, never present is " + TimelineCompiler.ERoleNotPresent);
        sb.AppendLine("              absent until spawn, absent after despawn; present-at-t is a later contain-only rule. Hor+ keeps fov vertical, never automatically fits");
        sb.AppendLine();
        sb.AppendLine("Omitted fields default to:");
        sb.AppendLine("  shot.type: " + shot.Type + " · shot.frame: " + shot.Frame + " · shot.from: " + shot.From + " · shot.ease: " + shot.Ease
            + " · fade: " + Num(cue.Fade) + " · speed: " + Num(cue.Speed) + " · volume: " + Num(cue.Volume) + " · scale: " + Num(cue.Scale) + " (duration: 0 = no auto-restore)");
        sb.AppendLine();
        sb.AppendLine("Shot params (shot.params, numeric; an unknown name is a compile error):");
        int nameWidth = ShotVocabulary.Params.Max(p => p.Name.Length);
        int unitWidth = ShotVocabulary.Params.Max(p => p.Unit.Length);
        foreach (var p in ShotVocabulary.Params)
            sb.AppendLine("  " + p.Name.PadRight(nameWidth) + "  " + p.Unit.PadRight(unitWidth) + "  " + (p.Default.HasValue ? Num(p.Default.Value) : "derived: " + ShotVocabulary.Derivations[p.Derivation]) + "  " + p.Meaning);
        sb.AppendLine();
        sb.AppendLine("Manifest capabilities:");
        foreach (var spec in CapabilitySpecs.All)
        {
            var syntax = spec.Values != null ? string.Join(" | ", spec.Values.Select(v => v.Value + " (" + v.Meaning + ")"))
                : spec.Kind + (spec.Separator == null ? "" : "; separator '" + spec.Separator + "'") + (spec.ItemSyntax == null ? "" : "; " + spec.ItemSyntax);
            sb.AppendLine("  " + spec.Key + "  " + syntax);
            sb.AppendLine("    " + spec.Description + " declared by " + spec.Declarer + "; undeclared: " + spec.Undeclared);
            foreach (var rule in spec.Rules)
                sb.AppendLine("    " + (rule.When ?? "any declared value") + ": " + rule.CueType + " " + rule.Field + " → " + TimelineCompiler.EUnsupportedByAdapter + " (" + (rule.Remedy ?? rule.Effect) + ")");
        }
        sb.AppendLine();
        sb.AppendLine("Compiler rules (the falsifier):");
        sb.AppendLine("  - authored files parse strictly: an unknown field is an error naming its JSON path");
        sb.AppendLine("    (cues[3].shot.durationSecond: unknown property); a missing required field names itself (version: required)");
        sb.AppendLine("  - every role/clip/location/audioId/shotType/frameType must exist in the game manifest");
        sb.AppendLine("  - operations on not-yet-spawned roles are errors (" + TimelineCompiler.ERoleNotPresent + "); overlapping camera shots are warnings (" + TimelineCompiler.WShotOverlap + ")");
        sb.AppendLine("  - a written shot.to must resolve to a manifest location or \"" + ShotVocabulary.CurrentAnchor + "\" for every shot type");
        sb.AppendLine("  - world.timescale with duration injects an explicit restore cue (injected=true in compiled output)");
        sb.AppendLine("  - cues stable-sort by t; same-time cues keep authored order; injected cues go last");
        sb.AppendLine("  - \"" + ShotVocabulary.CurrentAnchor + "\" is a reserved location id, never a manifest location");
        sb.AppendLine();
        sb.AppendLine("Diagnostic codes by severity (each also names its JSON path, e.g. cues[3].shot.to):");
        var codes = DiagnosticCodes().ToList();
        sb.AppendLine("  errors:   " + string.Join(" ", codes.Where(c => c.Severity == DiagnosticSeverity.Error).Select(c => c.Code)));
        sb.AppendLine("  warnings: " + string.Join(" ", codes.Where(c => c.Severity == DiagnosticSeverity.Warning).Select(c => c.Code)));
        sb.AppendLine("  (GD2002 is retired: role presence is the " + TimelineCompiler.ERoleNotPresent + " error above.)");
        sb.AppendLine();
        sb.AppendLine("Workflow (MCP tool names):");
        sb.AppendLine("  " + McpToolNames.GetManifest + " → author timeline → " + McpToolNames.ValidateTimeline + " → " + McpToolNames.PlayTimeline + " → " + McpToolNames.CaptureFrame + " → revise");
        sb.AppendLine("  managed production: " + McpToolNames.StudioState + " → " + McpToolNames.StudioValidate + " → " + McpToolNames.StudioProduce + " → " + McpToolNames.StudioJobStatus);
        sb.AppendLine("  full directing contract: " + McpToolNames.DirectorGuide + " · engine-facing constants: gd contract --json");
        sb.AppendLine("  gd measure <timeline.json> --frames N,A-B,... --out <new-directory> [--endpoint url] [--fps N] [--width W] [--height H]: exact rendered frames, raw camera/composition and Core horizon/shot provenance; CLI only");
        sb.AppendLine("  gd expect-record <timeline.json> --out <file.expect.json> --endpoint url [--fps N] [--sizes WxH,WxH] [--beats label=frame,...] [--work new-dir]: generated measured regression baseline; omitted beats reuse the output's beats");
        sb.AppendLine("  gd expect-check <timeline.json> --expect <file> --endpoint url [--work new-dir]: JSON numeric/state breaches; changed timeline hash is information; not an aesthetic check; CLI only");
        sb.AppendLine("  gd review <take-directory> [--safe-area preset|l,r,t,b]: JSON for latest Verified attempt; errors exit 1; non-Verified is reported, not judged");
        sb.AppendLine("  lost subject/with and protected side/near-plane cuts error; head-cut warns (framed-by-centre without targetHeight, coverage>1)");
        sb.AppendLine("  single-subject lateral crop never errors; angles or >" + Num(CompositionPolicy.Default.SingleSideFraction*100) + "% rect-width overflow warn; safe-area warns only");
        sb.AppendLine("  edge tolerance " + Num(CompositionPolicy.Default.EdgeTolerance) + " viewport; safe-area side tolerance " + Num(CompositionPolicy.Default.SafeSideTolerance) + " viewport (outward margins)");
        foreach(var preset in SafeArea.Presets)sb.AppendLine("  safe-area preset " + preset.Key + " (Core-owned l,r,t,b): " + string.Join(",",new[]{preset.Value.Left,preset.Value.Right,preset.Value.Top,preset.Value.Bottom}.Select(v=>v.ToString(CultureInfo.InvariantCulture))));
        sb.AppendLine("  legacy centre-only reviews warn only; cadence " + Num(ReviewPolicy.IntervalSeconds) + " s can miss cuts; aim uses each engine's legacy bounds; reviews use role-bounds-v1; contain is deferred");
        return sb.ToString();
    }

    /// <summary>The camera.shot payload signature, reflected from ShotSpec so a
    /// new field can never be forgotten here (focalLengthMm was, twice).</summary>
    private static string ShotFields()
    {
        var defaults = new ShotSpec();
        var tokens = new List<string>();
        foreach (var property in typeof(ShotSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
        {
            var name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            tokens.Add(property.Name switch
            {
                nameof(ShotSpec.Type) => name + ": " + string.Join("|", ShotVocabulary.ShotIds),
                nameof(ShotSpec.Frame) => name + ": " + string.Join("|", ShotVocabulary.FrameIds),
                nameof(ShotSpec.Ease) => name + ": " + string.Join("|", ShotVocabulary.Eases),
                nameof(ShotSpec.Subject) => name,
                nameof(ShotSpec.From) => name + ": " + defaults.From,
                _ => name + (IsOptional(property, property.GetValue(defaults)) ? "?" : ""),
            });
        }
        return string.Join(", ", tokens);
    }

    private static bool IsOptional(PropertyInfo property, object? value) =>
        Nullable.GetUnderlyingType(property.PropertyType) != null || (value == null && !property.PropertyType.IsValueType);

    /// <summary>Every GD#### constant on the compiler, with the severity its
    /// E*/W* constant name declares (E = error, W = warning).</summary>
    private static IEnumerable<(string Code, DiagnosticSeverity Severity)> DiagnosticCodes()
    {
        foreach (var field in typeof(TimelineCompiler).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.FieldType != typeof(string)) continue;
            if (field.GetValue(null) is not string code || !code.StartsWith("GD", StringComparison.Ordinal)) continue;
            yield return (code, field.Name.StartsWith("W", StringComparison.Ordinal) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error);
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
