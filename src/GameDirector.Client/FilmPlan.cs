using System.Security.Cryptography;
using System.Text;
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
public sealed record PreparedShot(string Id, string Purpose, double Start, double End, TimelineAsset Timeline, string CacheKey);
public sealed record PreparedFilm(FilmPlan Plan, string ManifestHash, List<PreparedShot> Shots);

public static class FilmCompiler
{
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DslJson.Serialize(value)))).ToLowerInvariant();

    public static PreparedFilm Prepare(FilmPlan input, CapabilityManifest manifest)
    {
        var plan = DslJson.Deserialize<FilmPlan>(DslJson.Serialize(input));
        if (plan.Version != 1 || string.IsNullOrWhiteSpace(plan.Title) || plan.Scenes == null || plan.Scenes.Count is < 1 or > FilmLimits.MaxScenes)
            throw new ArgumentException("Film requires a title, version 1 and 1–" + FilmLimits.MaxScenes + " scenes.");
        var geometryError = CaptureContract.CheckGeometry(plan.FrameRate, plan.Width, plan.Height);
        if (geometryError != null)
            throw new ArgumentException("Invalid film frame rate or dimensions: " + geometryError + ".");
        if (manifest.Capabilities == null || !manifest.Capabilities.TryGetValue(CapabilityKeys.DirectorMode, out var mode) || mode != DirectorModes.OfflineSandbox ||
            !manifest.Capabilities.TryGetValue(CapabilityKeys.PresentationSourceFingerprint, out var fingerprint) || string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("Connect an offline presentation adapter with a source fingerprint before producing a film.");
        var hash = Hash(manifest);
        var shots = new List<PreparedShot>();
        var ids = new HashSet<string>();
        var sceneIds = new HashSet<string>();
        foreach (var scene in plan.Scenes)
        {
            if (scene == null || !ValidId(scene.Id) || !sceneIds.Add(scene.Id) || scene.Shots == null || scene.Shots.Count == 0 || scene.Performance == null || scene.Performance.Count > FilmLimits.MaxPerformanceCues)
                throw new ArgumentException("Scenes need unique stable IDs, a performance and at least one shot.");
            if (scene.Performance.Any(c => c == null || c.Type == CueTypes.CameraShot || c.Type == CueTypes.AudioPlay || c.Type == CueTypes.AudioStop))
                throw new ArgumentException("Scene performance contains only actor, marker and world cues. Cameras belong to shots; picture capture does not record live audio.");
            foreach (var shot in scene.Shots)
            {
                if (shot == null || !ValidId(shot.Id) || !ids.Add(shot.Id) || string.IsNullOrWhiteSpace(shot.Purpose) || shot.Camera == null ||
                    !double.IsFinite(shot.Start) || !double.IsFinite(shot.End) || shot.Start < 0 || shot.End <= shot.Start || shot.End > FilmLimits.MaxOutputSeconds ||
                    !Aligned(shot.Start, plan.FrameRate) || !Aligned(shot.End, plan.FrameRate))
                    throw new ArgumentException("Shots need globally unique stable IDs, an editorial purpose, a camera, and frame-aligned start/end within " + (int)FilmLimits.MaxOutputSeconds + " seconds.");
                var timeline = new TimelineAsset { Id = scene.Id + "/" + shot.Id, Title = shot.Purpose };
                timeline.Cues.AddRange(scene.Performance.Where(c => c.T < shot.End).Select(Clone));
                var camera = DslJson.Deserialize<ShotSpec>(DslJson.Serialize(shot.Camera));
                camera.DurationSeconds = shot.End - shot.Start;
                // Pre-roll preserves performance continuity but is excluded from the edit.
                // Use an explicit static camera until the authored shot begins.
                if (shot.Start > 0)
                {
                    var pre = DslJson.Deserialize<ShotSpec>(DslJson.Serialize(camera));
                    var initialRoles = manifest.Roles.Where(r => r.PresentAtStart).Select(r => r.Id).ToHashSet();
                    foreach (var cue in scene.Performance.Where(c => c.T == 0))
                    {
                        if (cue.Type == CueTypes.ActorSpawn) initialRoles.Add(cue.Role);
                        if (cue.Type == CueTypes.ActorDespawn) initialRoles.Remove(cue.Role);
                    }
                    if (!initialRoles.Contains(pre.Subject)) pre.Subject = manifest.Roles.FirstOrDefault(r => initialRoles.Contains(r.Id))?.Id
                        ?? throw new ArgumentException(shot.Id + ": pre-roll needs a role bound or spawned at t=0; this engine cannot capture an empty stage prefix.");
                    if (pre.LookAt != null && manifest.FindRole(pre.LookAt) != null && !initialRoles.Contains(pre.LookAt)) pre.LookAt = null;
                    pre.Type = ShotVocabulary.DefaultShotId; pre.DurationSeconds = shot.Start;
                    timeline.Cues.Add(new Cue { T = 0, Type = CueTypes.CameraShot, Shot = pre });
                }
                timeline.Cues.Add(new Cue { T = shot.Start, Type = CueTypes.CameraShot, Shot = camera });
                timeline.Cues.Add(new Cue { T = shot.End, Type = CueTypes.Marker, Label = "shot-end" });
                // A temporary time-scale restoration after the cut must not extend this take.
                foreach (var cue in timeline.Cues.Where(c => c.Type == CueTypes.WorldTimeScale && c.Duration > 0))
                    cue.Duration = Math.Min(cue.Duration, shot.End - cue.T);
                var compiled = TimelineCompiler.Compile(timeline, manifest);
                if (compiled.HasErrors || compiled.Diagnostics.Any(d => d.Code == TimelineCompiler.WRoleNotPresent))
                    throw new ArgumentException(shot.Id + ": " + string.Join("; ", compiled.Diagnostics));
                // Identity/purpose do not affect pixels; editorial changes must not re-render.
                var pixelTimeline = DslJson.Deserialize<TimelineAsset>(DslJson.Serialize(timeline));
                pixelTimeline.Id = "picture"; pixelTimeline.Title = "picture";
                var key = Hash(new { timeline = pixelTimeline, manifest = hash, plan.FrameRate, plan.Width, plan.Height });
                shots.Add(new PreparedShot(shot.Id, shot.Purpose, shot.Start, shot.End, timeline, key));
            }
        }
        if (shots.Count > FilmLimits.MaxShots || shots.Sum(s => s.End - s.Start) > FilmLimits.MaxOutputSeconds || shots.Sum(s => s.End) > FilmLimits.MaxCaptureSeconds)
            throw new ArgumentException("Film exceeds " + FilmLimits.MaxShots + " shots, " + (int)(FilmLimits.MaxOutputSeconds / 60) + " minutes of output or " + (int)(FilmLimits.MaxCaptureSeconds / 60) + " minutes of pre-roll capture.");
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
                a.Duration <= 0 || a.At + a.Duration > duration + .00001 || a.Volume < 0 || a.Volume > FilmLimits.MaxVolume || a.FadeIn < 0 || a.FadeOut < 0 || a.FadeIn + a.FadeOut > a.Duration)
                throw new ArgumentException("Audio cues need unique IDs, imported media IDs, valid bus, gain, fades and timing within the final film.");
        double previousEnd = 0;
        foreach (var caption in plan.Subtitles)
        {
            if (caption == null || !double.IsFinite(caption.Start) || !double.IsFinite(caption.End) || caption.Start < previousEnd || caption.End <= caption.Start ||
                caption.End > duration + .00001 || string.IsNullOrWhiteSpace(caption.Text) || caption.Text.Length > FilmLimits.MaxSubtitleLength || caption.Text.Contains('\0'))
                throw new ArgumentException("Captions must be ordered, non-overlapping, nonempty and within the final film.");
            previousEnd = caption.End;
        }
    }
    private static Cue Clone(Cue cue) => DslJson.Deserialize<Cue>(DslJson.Serialize(cue));
    public static FilmPlan Read(string path) => System.Text.Json.JsonSerializer.Deserialize<FilmPlan>(File.ReadAllText(path),
        new System.Text.Json.JsonSerializerOptions(DslJson.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
        ?? throw new ArgumentException("Empty film script.");
    private static bool Aligned(double t, int fps) => Math.Abs(t * fps - Math.Round(t * fps)) < .00001;
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
