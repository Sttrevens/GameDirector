using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

public sealed class FilmPlan
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "Untitled";
    public int FrameRate { get; set; } = FilmDefaults.FrameRate;
    public int Width { get; set; } = FilmDefaults.Width;
    public int Height { get; set; } = FilmDefaults.Height;
    public List<FilmScene> Scenes { get; set; } = new();
    public List<FilmAudioCue> Audio { get; set; } = new();
    public List<FilmSubtitle> Subtitles { get; set; } = new();
}
// Sound and captions use final edited-film time, independently of shot pre-roll.
public sealed class FilmAudioCue
{
    public string Id { get; set; } = "";
    public string MediaId { get; set; } = "";
    public string Bus { get; set; } = "sfx";
    public double At { get; set; }
    public double SourceStart { get; set; }
    public double Duration { get; set; }
    public double Volume { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
}
public sealed class FilmSubtitle
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}
public sealed class FilmScene
{
    public string Id { get; set; } = "";
    // Every camera replays this performance from the same initial engine state.
    public List<Cue> Performance { get; set; } = new();
    public List<FilmShot> Shots { get; set; } = new();
}
public sealed class FilmShot
{
    public string Id { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Purpose { get; set; } = "";
    public ShotSpec Camera { get; set; } = new();
}
public sealed record PreparedShot(string Id, string Purpose, double Start, double End, TimelineAsset Timeline, string CacheKey)
{
    public int CaptureFromFrame { get; init; }
}
public sealed record PreparedFilm(FilmPlan Plan, string ManifestHash, List<PreparedShot> Shots);

public static class FilmCompiler
{
    /// <summary>Take-cache generation. The generation is the cache NAMESPACE
    /// (cache/format.N/<project>/<key>), never part of the hashed key: what a
    /// format change makes unreachable must be identifiable as dead by its
    /// location, so prune can reclaim whole old generations. Bump deliberately
    /// when pipeline or identity changes should invalidate cached takes.</summary>
    public const int CacheFormat = 4;

    public static PreparedFilm Prepare(FilmPlan input, CapabilityManifest manifest, MediaProfile? profile = null)
    {
        DirectorOperations.RequireValidManifest(manifest);
        // Encoded pixels depend on the encoding profile as much as on the
        // timeline: the profile is cache identity, never restated per call site.
        profile ??= MediaProfile.Default;
        var plan = DslJson.Deserialize<FilmPlan>(DslJson.Serialize(input));
        if (plan.Version != 1 || string.IsNullOrWhiteSpace(plan.Title) || plan.Scenes == null || plan.Scenes.Count is < 1 or > FilmLimits.MaxScenes)
            throw new ArgumentException("Film requires a title, version 1 and 1–" + FilmLimits.MaxScenes + " scenes.");
        var geometryError = CaptureContract.CheckGeometry(plan.FrameRate, plan.Width, plan.Height);
        if (geometryError != null)
            throw new ArgumentException("Invalid film frame rate or dimensions: " + geometryError + ".");
        if (manifest.Capabilities == null || !manifest.Capabilities.TryGetValue(CapabilityKeys.DirectorMode, out var mode) || mode != DirectorModes.OfflineSandbox ||
            !manifest.Capabilities.TryGetValue(CapabilityKeys.PresentationSourceFingerprint, out var fingerprint) || string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("Connect an offline presentation adapter with a source fingerprint before producing a film.");
        // Full manifest identity, fingerprint included: pixels depend on the live source.
        var hash = ContentIdentity.Of(manifest);
        var shots = new List<PreparedShot>();
        var ids = new HashSet<string>();
        var sceneIds = new HashSet<string>();
        foreach (var (scene, i) in plan.Scenes.Select((s, i) => (s, i)))
        {
            if (scene == null || !ValidId(scene.Id) || !sceneIds.Add(scene.Id) || scene.Shots == null || scene.Shots.Count == 0 || scene.Performance == null || scene.Performance.Count > FilmLimits.MaxPerformanceCues)
                throw new ArgumentException("Scenes need unique stable IDs, a performance and at least one shot.");
            for (var j = 0; j < scene.Performance.Count; j++)
            {
                var performanceCue = scene.Performance[j];
                if (performanceCue == null || performanceCue.Type == CueTypes.CameraShot || performanceCue.Type == CueTypes.AudioPlay || performanceCue.Type == CueTypes.AudioStop)
                    throw new ArgumentException("scenes[" + i + "].performance[" + j + "]: scene performance contains only actor, marker and world cues. Cameras belong to shots; picture capture does not record live audio.");
            }
            foreach (var (shot, k) in scene.Shots.Select((s, k) => (s, k)))
            {
                if (shot == null || !ValidId(shot.Id) || !ids.Add(shot.Id) || string.IsNullOrWhiteSpace(shot.Purpose) || shot.Camera == null ||
                    !double.IsFinite(shot.Start) || !double.IsFinite(shot.End) || shot.Start < 0 || shot.End <= shot.Start || shot.End > FilmLimits.MaxOutputSeconds ||
                    !FrameClock.IsAligned(shot.Start, plan.FrameRate) || !FrameClock.IsAligned(shot.End, plan.FrameRate))
                    throw new ArgumentException("Shots need globally unique stable IDs, an editorial purpose, a camera, and frame-aligned start/end within " + (int)FilmLimits.MaxOutputSeconds + " seconds.");
                shot.Start=FrameClock.Snap(shot.Start,plan.FrameRate);shot.End=FrameClock.Snap(shot.End,plan.FrameRate);
                if(shot.End<=shot.Start)throw new ArgumentException("Shots must contain at least one frame.");
                var timeline = new TimelineAsset { Id = scene.Id + "/" + shot.Id, Title = shot.Purpose };
                // The synthetic per-shot timeline's cue indices mean nothing to the
                // author, so each is mapped back to its FilmPlan source path and
                // diagnostics are rewritten before being raised.
                var sourceMap = new Dictionary<int, string>();
                for (var p = 0; p < scene.Performance.Count; p++)
                {
                    var cue = scene.Performance[p];
                    if (cue.T >= shot.End) continue;
                    sourceMap[timeline.Cues.Count] = "scenes[" + i + "].performance[" + p + "]";
                    timeline.Cues.Add(Clone(cue));
                }
                var camera = DslJson.Deserialize<ShotSpec>(DslJson.Serialize(shot.Camera));
                camera.DurationSeconds = shot.End - shot.Start;
                // Replay the whole performance, but no pre-roll pixels need a camera.
                var captureFromFrame = FrameClock.FrameIndex(shot.Start, plan.FrameRate);
                sourceMap[timeline.Cues.Count] = "scenes[" + i + "].shots[" + k + "].camera";
                timeline.Cues.Add(new Cue { T = shot.Start, Type = CueTypes.CameraShot, Shot = camera });
                sourceMap[timeline.Cues.Count] = "scenes[" + i + "].shots[" + k + "] (generated shot-end marker)";
                timeline.Cues.Add(new Cue { T = shot.End, Type = CueTypes.Marker, Label = "shot-end" });
                // A temporary time-scale restoration after the cut must not extend this take.
                foreach (var cue in timeline.Cues.Where(c => c.Type == CueTypes.WorldTimeScale && c.Duration > 0))
                    cue.Duration = Math.Min(cue.Duration, shot.End - cue.T);
                // Every prepared shot is a take: it must pass the same Core
                // admission gate the engine runs, so validate/readiness fail early.
                var admission = TakeAdmission.Admit(timeline, manifest, plan.FrameRate, plan.Width, plan.Height, captureFromFrame);
                if (!admission.Ok)
                    throw new ArgumentException(string.Join("; ", admission.GateErrors.Concat(
                        admission.Compiled.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Select(d => FilmCoordinates(d, sourceMap).ToString()))));
                // Identity/purpose do not affect pixels; editorial changes must not re-render.
                var pixelTimeline = DslJson.Deserialize<TimelineAsset>(DslJson.Serialize(timeline));
                pixelTimeline.Id = "picture"; pixelTimeline.Title = "picture";
                // The format generation is the namespace (cache/format.N/...),
                // not the key: only identity inputs are hashed.
                var key = ContentIdentity.Of(new { timeline = pixelTimeline, manifest = hash, captureFromFrame, plan.FrameRate, plan.Width, plan.Height,
                    codec = profile.VideoCodec, preset = profile.Preset, crf = profile.Crf, pixelFormat = profile.PixelFormat });
                shots.Add(new PreparedShot(shot.Id, shot.Purpose, shot.Start, shot.End, timeline, key) { CaptureFromFrame = captureFromFrame });
            }
        }
        if (shots.Count > FilmLimits.MaxShots || shots.Sum(s => s.End - s.Start) > FilmLimits.MaxOutputSeconds || shots.Sum(s => s.End) > FilmLimits.MaxCaptureSeconds)
            throw new ArgumentException("Film exceeds " + FilmLimits.MaxShots + " shots, " + (int)(FilmLimits.MaxOutputSeconds / 60) + " minutes of output or " + (int)(FilmLimits.MaxCaptureSeconds / 60) + " minutes of replay (including pre-roll).");
        ValidateSound(plan, shots.Sum(s => s.End - s.Start));
        return new PreparedFilm(plan, hash, shots);
    }
    public static void ValidateSound(FilmPlan plan, double duration)
    {
        if (plan.Audio == null || plan.Subtitles == null || plan.Audio.Count > FilmLimits.MaxAudioCues || plan.Subtitles.Count > FilmLimits.MaxSubtitles)
            throw new ArgumentException("Film supports up to " + FilmLimits.MaxAudioCues + " audio cues and " + FilmLimits.MaxSubtitles + " captions.");
        var ids = new HashSet<string>();
        foreach (var a in plan.Audio)
            if (a == null || !ValidId(a.Id) || !ids.Add(a.Id) || !System.Text.RegularExpressions.Regex.IsMatch(a.MediaId ?? "", "^[a-f0-9]{64}$") ||
                !(a.Bus is "dialogue" or "music" or "sfx") || !double.IsFinite(a.At) || !double.IsFinite(a.SourceStart) || !double.IsFinite(a.Duration) ||
                !double.IsFinite(a.Volume) || !double.IsFinite(a.FadeIn) || !double.IsFinite(a.FadeOut) || a.At < 0 || a.SourceStart < 0 ||
                a.Duration <= 0 || a.At + a.Duration > duration + FrameClock.AlignmentToleranceFrames / plan.FrameRate || a.Volume < 0 || a.Volume > FilmLimits.MaxVolume || a.FadeIn < 0 || a.FadeOut < 0 || a.FadeIn + a.FadeOut > a.Duration)
                throw new ArgumentException("Audio cues need unique IDs, imported media IDs, valid bus, gain, fades and timing within the final film.");
        double previousEnd = 0;
        foreach (var caption in plan.Subtitles)
        {
            if (caption == null || !double.IsFinite(caption.Start) || !double.IsFinite(caption.End) || caption.Start < previousEnd || caption.End <= caption.Start ||
                caption.End > duration + FrameClock.AlignmentToleranceFrames / plan.FrameRate || string.IsNullOrWhiteSpace(caption.Text) || caption.Text.Length > FilmLimits.MaxSubtitleLength || caption.Text.Contains('\0'))
                throw new ArgumentException("Captions must be ordered, non-overlapping, nonempty and within the final film.");
            previousEnd = caption.End;
        }
    }
    private static Cue Clone(Cue cue) => DslJson.Deserialize<Cue>(DslJson.Serialize(cue));

    /// <summary>Rewrite a synthetic-timeline diagnostic into the FilmPlan
    /// coordinates the author wrote: a performance cue maps to
    /// scenes[i].performance[j]; the shot's camera cue to scenes[i].shots[k].camera
    /// (the synthetic ".shot" hop collapses: cues[7].shot.to becomes
    /// scenes[0].shots[1].camera.to); generated cues carry a label instead of a
    /// field that does not exist in the authored document.</summary>
    private static Diagnostic FilmCoordinates(Diagnostic d, IReadOnlyDictionary<int, string> sourceMap)
    {
        if (d.CueIndex < 0 || !sourceMap.TryGetValue(d.CueIndex, out var source)) return d;
        var suffix = "";
        if (!string.IsNullOrEmpty(d.Path) && d.Path.StartsWith("cues[", StringComparison.Ordinal))
        {
            var close = d.Path.IndexOf(']');
            if (close >= 0) suffix = d.Path.Substring(close + 1);
        }
        if ((source.EndsWith(".camera", StringComparison.Ordinal) || source.Contains("(generated")) &&
            suffix.StartsWith(".shot", StringComparison.Ordinal))
            suffix = suffix.Length == ".shot".Length ? "" : suffix.Substring(".shot".Length);
        return new Diagnostic { Severity = d.Severity, Code = d.Code, Message = d.Message, CueIndex = d.CueIndex, Path = source + suffix };
    }

    /// <summary>Read an authored film script strictly: unknown fields and a missing
    /// version fail with the JSON path, never with a silently applied default.</summary>
    public static FilmPlan Read(string path) => DslJson.LoadAuthored<FilmPlan>(path);
    public static bool ValidId(string? id) => id != null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}$");

    public static FilmPlan Starter(CapabilityManifest manifest)
    {
        var role = manifest.Roles.FirstOrDefault(r => r.PresentAtStart) ?? throw new ArgumentException("No role is currently bound. Prepare a presentation stage first.");
        var location = manifest.Locations.FirstOrDefault() ?? throw new ArgumentException("No camera anchor is bound.");
        return new FilmPlan { Title = manifest.Game + " · First scene", Scenes = new() {
            new FilmScene { Id = "scene-01", Shots = new() { new FilmShot { Id = "shot-01", Start = 0, End = 3, Purpose = "Establish the character and space", Camera = new ShotSpec {
                Type = ShotVocabulary.DefaultShotId, Subject = role.Id, Frame = manifest.FrameTypes.Contains(ShotVocabulary.DefaultFrameId) ? ShotVocabulary.DefaultFrameId : manifest.FrameTypes.First(), From = location.Id } } } } } };
    }
}
