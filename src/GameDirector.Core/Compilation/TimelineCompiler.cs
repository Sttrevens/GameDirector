using System;
using System.Collections.Generic;
using GameDirector.Core.Dsl;

namespace GameDirector.Core.Compilation
{
    public enum DiagnosticSeverity { Warning, Error }

    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity;
        public string Code;
        public string Message;
        public int CueIndex; // -1 = asset level

        public override string ToString()
        {
            string where = CueIndex >= 0 ? "cue[" + CueIndex + "]" : "asset";
            return Severity + " " + Code + " @ " + where + ": " + Message;
        }
    }

    public sealed class CompileResult
    {
        public CompiledTimeline Timeline; // null when any Error exists
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < Diagnostics.Count; i++)
                    if (Diagnostics[i].Severity == DiagnosticSeverity.Error) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Validates an authored timeline against a capability manifest and produces
    /// a deterministic, ordered, ready-to-play <see cref="CompiledTimeline"/>.
    /// This is the kernel's falsifier: anything the game cannot express fails here,
    /// before a single frame is rendered.
    /// </summary>
    public static class TimelineCompiler
    {
        public const string SupportedVersion = "0.1";
        private const string CurrentKeyword = "current";

        // Error codes (stable; tests and tooling match on these).
        public const string EUnsupportedVersion = "GD1000";
        public const string EUnknownRole = "GD1001";
        public const string EUnknownClip = "GD1002";
        public const string EUnknownLocation = "GD1003";
        public const string EUnknownShotType = "GD1004";
        public const string EBadTime = "GD1005";
        public const string EUnknownCueType = "GD1006";
        public const string EUnknownFrameType = "GD1007";
        public const string EUnknownAudio = "GD1008";
        public const string EBadValue = "GD1009";
        public const string EMissingId = "GD1010";
        public const string WShotOverlap = "GD2001";
        public const string WRoleNotPresent = "GD2002";
        public const string WEmptyLabel = "GD2003";

        public static CompileResult Compile(TimelineAsset asset, CapabilityManifest manifest)
        {
            var result = new CompileResult();
            if (asset == null) { Error(result, EBadValue, -1, "timeline asset is null"); return result; }
            if (manifest == null) { Error(result, EBadValue, -1, "capability manifest is null"); return result; }

            if (asset.Version != SupportedVersion)
                Error(result, EUnsupportedVersion, -1, "version '" + asset.Version + "' is not supported (expected " + SupportedVersion + ")");
            if (string.IsNullOrEmpty(asset.Id))
                Error(result, EMissingId, -1, "timeline id is required");

            ValidateManifest(result, manifest);
            if (result.HasErrors) return result;
            var cues = asset.Cues ?? new List<Cue>();
            var compiled = new List<Cue>(cues.Count + 2);
            var present = new Dictionary<string, bool>(); // role -> currently present in scene
            if (manifest.Roles != null)
                foreach (var r in manifest.Roles) if (r != null && r.Id != null) present[r.Id] = r.PresentAtStart;

            // Authored order defines fire order within equal T (stable sort later).
            var indexed = new List<Cue>(cues.Count);
            for (int i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                if (c == null) { Error(result, EBadValue, i, "null cue"); continue; }
                c = c.Snapshot();
                c.SourceIndex = i;
                c.Injected = false;
                indexed.Add(c);
            }
            indexed.Sort(StableByTime);

            double duration = 0;
            double openShotEnd = -1; // end time of the current shot window; -1 = no active window
            double openShotT = 0;

            foreach (var cue in indexed)
            {
                if (double.IsNaN(cue.T) || double.IsInfinity(cue.T) || cue.T < 0)
                {
                    Error(result, EBadTime, cue.SourceIndex, "cue time must be finite and >= 0, got " + cue.T);
                    continue;
                }
                if (cue.T > duration) duration = cue.T;

                switch (cue.Type)
                {
                    case CueTypes.CameraShot:
                        ValidateShot(result, manifest, cue);
                        if (cue.Shot != null) {
                            RequireRolePresent(result, manifest, cue, cue.Shot.Subject, present);
                            if (cue.Shot.LookAt != null) RequireRolePresent(result, manifest, cue, cue.Shot.LookAt, present);
                        }
                        if (openShotEnd > cue.T + 1e-6)
                            Warn(result, WShotOverlap, cue.SourceIndex,
                                "camera shot at t=" + cue.T + " cuts into the shot started at t=" + openShotT + " (window ends at " + openShotEnd + "; later shot wins)");
                        openShotT = cue.T;
                        // durationSeconds=0 means "hold until the next shot": replaced, not overlapped.
                        openShotEnd = cue.Shot != null && cue.Shot.DurationSeconds > 0
                            ? cue.T + cue.Shot.DurationSeconds
                            : -1;
                        if (openShotEnd > duration) duration = openShotEnd;
                        break;

                    case CueTypes.ActorSpawn:
                        RequireRole(result, manifest, cue, cue.Role);
                        RequireLocation(result, manifest, cue, cue.Location, "Location");
                        if (cue.Role != null && present.TryGetValue(cue.Role, out var already) && already)
                            Error(result, EBadValue, cue.SourceIndex, "role is already present; despawn before spawn");
                        ValidateHeading(result, manifest, cue, false, present);
                        if (cue.Role != null && present.ContainsKey(cue.Role)) present[cue.Role] = true;
                        break;

                    case CueTypes.ActorDespawn:
                        RequireRolePresent(result, manifest, cue, cue.Role, present);
                        if (cue.Role != null && present.ContainsKey(cue.Role)) present[cue.Role] = false;
                        break;

                    case CueTypes.ActorAnim:
                        ValidateAnim(result, manifest, cue, present);
                        break;

                    case CueTypes.ActorMove:
                        RequireRolePresent(result, manifest, cue, cue.Role, present);
                        RequireLocation(result, manifest, cue, cue.To, "To");
                        if (!Finite(cue.Speed) || cue.Speed <= 0) Error(result, EBadValue, cue.SourceIndex, "actor.move Speed must be > 0");
                        ValidateHeading(result, manifest, cue, false, present);
                        break;

                    case CueTypes.ActorFace:
                        RequireRolePresent(result, manifest, cue, cue.Role, present);
                        ValidateHeading(result, manifest, cue, true, present);
                        break;

                    case CueTypes.AudioPlay:
                        if (manifest.FindAudio(cue.AudioId) == null)
                            Error(result, EUnknownAudio, cue.SourceIndex, "unknown audio id '" + cue.AudioId + "'");
                        if (!Finite(cue.Volume) || cue.Volume < 0f || cue.Volume > 1f)
                            Error(result, EBadValue, cue.SourceIndex, "audio.play Volume must be in [0,1]");
                        break;

                    case CueTypes.AudioStop:
                        if (manifest.FindAudio(cue.AudioId) == null)
                            Error(result, EUnknownAudio, cue.SourceIndex, "unknown audio id '" + cue.AudioId + "'");
                        break;

                    case CueTypes.WorldTimeScale:
                        if (!Finite(cue.Scale) || cue.Scale <= 0f || cue.Scale > 10f) Error(result, EBadValue, cue.SourceIndex, "world.timescale Scale must be > 0");
                        if (!Finite(cue.Duration) || cue.Duration < 0 || !Finite(cue.T + cue.Duration)) Error(result, EBadValue, cue.SourceIndex, "world.timescale Duration must be >= 0");
                        break;

                    case CueTypes.Marker:
                        if (string.IsNullOrEmpty(cue.Label))
                            Warn(result, WEmptyLabel, cue.SourceIndex, "marker without label");
                        break;

                    default:
                        Error(result, EUnknownCueType, cue.SourceIndex, "unknown cue type '" + cue.Type + "'");
                        break;
                }

                compiled.Add(cue);

                // Time-limited timescale becomes an explicit restore cue: the player stays dumb,
                // and the compiled artifact shows the full deterministic event list.
                if (cue.Type == CueTypes.WorldTimeScale && cue.Duration > 0)
                {
                    var restore = new Cue
                    {
                        T = cue.T + cue.Duration,
                        Type = CueTypes.WorldTimeScale,
                        Scale = 1f,
                        SourceIndex = -1,
                        Injected = true
                    };
                    // A newer authored scale owns the channel; its predecessor may
                    // not restore over it, including an equal-time boundary.
                    bool superseded = indexed.Exists(c => c != cue && c.Type == CueTypes.WorldTimeScale
                        && (c.T > cue.T || (c.T == cue.T && c.SourceIndex > cue.SourceIndex)) && c.T <= restore.T);
                    if (!superseded) compiled.Add(restore);
                    if (!superseded && restore.T > duration) duration = restore.T;
                }
            }

            compiled.Sort(StableByTime);

            if (!result.HasErrors)
            {
                result.Timeline = new CompiledTimeline
                {
                    Id = asset.Id,
                    Title = asset.Title,
                    OrderedCues = compiled,
                    Duration = duration
                };
            }
            return result;
        }

        private static void ValidateShot(CompileResult result, CapabilityManifest manifest, Cue cue)
        {
            if (cue.Shot == null)
            {
                Error(result, EBadValue, cue.SourceIndex, "camera.shot requires a shot payload");
                return;
            }
            var s = cue.Shot;

            if (!manifest.ShotTypes.Contains(s.Type))
                Error(result, EUnknownShotType, cue.SourceIndex, "unknown shot type '" + s.Type + "' (manifest declares: " + string.Join("/", manifest.ShotTypes.ToArray()) + ")");
            if (!manifest.FrameTypes.Contains(s.Frame))
                Error(result, EUnknownFrameType, cue.SourceIndex, "unknown frame type '" + s.Frame + "' (manifest declares: " + string.Join("/", manifest.FrameTypes.ToArray()) + ")");

            if (string.IsNullOrEmpty(s.Subject))
                Error(result, EUnknownRole, cue.SourceIndex, "camera.shot requires a subject role");
            else if (manifest.FindRole(s.Subject) == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown subject role '" + s.Subject + "'");

            if (s.LookAt != null && manifest.FindRole(s.LookAt) == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown lookAt role '" + s.LookAt + "'");

            RequireLocation(result, manifest, cue, s.From, "shot.from", allowCurrent: true);
            // Whether a destination anchor is required is vocabulary semantics,
            // not a per-callsite type list. Custom types declare their own.
            if (ShotVocabulary.RequiresTarget(s.Type) == true)
                RequireLocation(result, manifest, cue, s.To, "shot.to", allowCurrent: true);

            if (!Finite(s.DurationSeconds) || s.DurationSeconds < 0 || !Finite(cue.T + s.DurationSeconds))
                Error(result, EBadValue, cue.SourceIndex, "shot durationSeconds must be >= 0");
            if (s.Fov.HasValue && (!Finite(s.Fov.Value) || s.Fov.Value < 5f || s.Fov.Value > 170f))
                Error(result, EBadValue, cue.SourceIndex, "shot fov out of range [5,170]: " + s.Fov.Value);
            if (s.FocalLengthMm.HasValue && (!Finite(s.FocalLengthMm.Value) || s.FocalLengthMm.Value <= 0))
                Error(result, EBadValue, cue.SourceIndex, "focalLengthMm must be finite and positive");
            if (s.Fov.HasValue && s.FocalLengthMm.HasValue)
                Error(result, EBadValue, cue.SourceIndex, "choose fov or focalLengthMm, not both");
            if (!ShotVocabulary.ValidEase(s.Ease))
                Error(result, EBadValue, cue.SourceIndex, "unknown easing");
            if (s.Params != null) foreach (var pair in s.Params)
                if (!Finite(pair.Value)) Error(result, EBadValue, cue.SourceIndex, "shot parameters must be finite");

        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void ValidateHeading(CompileResult r, CapabilityManifest m, Cue c, bool required, Dictionary<string, bool> present)
        {
            if (string.IsNullOrWhiteSpace(c.HeadingTo)) {
                if (required) Error(r, EBadValue, c.SourceIndex, "headingTo is required");
            } else if (m.FindRole(c.HeadingTo) == null && m.FindLocation(c.HeadingTo) == null)
                Error(r, EUnknownLocation, c.SourceIndex, "unknown headingTo '" + c.HeadingTo + "'");
            else if (m.FindRole(c.HeadingTo) != null) RequireRolePresent(r, m, c, c.HeadingTo, present);
        }

        private static void ValidateManifest(CompileResult r, CapabilityManifest m)
        {
            if (m.ManifestVersion != SupportedVersion) Error(r, EUnsupportedVersion, -1, "unsupported manifest version");
            if (m.Roles == null || m.Actors == null || m.Locations == null || m.Audio == null || m.ShotTypes == null || m.FrameTypes == null) {
                Error(r, EBadValue, -1, "manifest collections must not be null"); return;
            }
            ValidateIds(r, m.Roles.ConvertAll(x => x?.Id), "role");
            ValidateIds(r, m.Actors.ConvertAll(x => x?.Id), "actor");
            ValidateIds(r, m.Locations.ConvertAll(x => x?.Id), "location");
            ValidateIds(r, m.Audio.ConvertAll(x => x?.Id), "audio");
            ValidateIds(r, m.ShotTypes, "shot type");
            ValidateIds(r, m.FrameTypes, "frame type");
            if (r.HasErrors) return;
            foreach (var role in m.Roles)
                if (m.FindActor(role.DefaultActor) == null) Error(r, EBadValue, -1, "role '" + role.Id + "' has no declared actor");
            foreach (var a in m.Actors) {
                if (a.Clips == null) Error(r, EBadValue, -1, "clips must not be null");
                else ValidateIds(r, a.Clips, "clip on " + a.Id);
            }
            foreach (var loc in m.Locations) {
                if (loc.Id == CurrentKeyword) Error(r, EBadValue, -1, "current is a reserved location");
                if (loc.Position != null && (loc.Position.Length != 3 || Array.Exists(loc.Position, v => !Finite(v))))
                    Error(r, EBadValue, -1, "location position must be three finite numbers");
                if (!Finite(loc.HeadingDeg)) Error(r, EBadValue, -1, "heading must be finite");
            }
        }

        private static void ValidateIds(CompileResult r, List<string> ids, string kind)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids) if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                Error(r, EBadValue, -1, "empty or duplicate " + kind + " id");
        }

        private static void ValidateAnim(CompileResult result, CapabilityManifest manifest, Cue cue, Dictionary<string, bool> present)
        {
            if (!Finite(cue.Fade) || cue.Fade < 0) Error(result, EBadValue, cue.SourceIndex, "animation fade must be finite and >= 0 seconds");
            var role = RequireRolePresent(result, manifest, cue, cue.Role, present);
            if (role == null) return;
            if (string.IsNullOrEmpty(cue.Clip))
            {
                Error(result, EUnknownClip, cue.SourceIndex, "actor.anim requires a clip");
                return;
            }
            var actor = manifest.FindActor(role.DefaultActor);
            if (actor == null || actor.Clips == null || !actor.Clips.Contains(cue.Clip))
                Error(result, EUnknownClip, cue.SourceIndex,
                    "clip '" + cue.Clip + "' is not declared on actor '" + role.DefaultActor + "'");
        }

        private static RoleDescriptor RequireRole(CompileResult result, CapabilityManifest manifest, Cue cue, string roleId)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                Error(result, EUnknownRole, cue.SourceIndex, cue.Type + " requires a role");
                return null;
            }
            var role = manifest.FindRole(roleId);
            if (role == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown role '" + roleId + "'");
            return role;
        }

        private static RoleDescriptor RequireRolePresent(CompileResult result, CapabilityManifest manifest, Cue cue, string roleId, Dictionary<string, bool> present)
        {
            var role = RequireRole(result, manifest, cue, roleId);
            if (role != null && present.TryGetValue(role.Id, out bool isPresent) && !isPresent)
                Warn(result, WRoleNotPresent, cue.SourceIndex,
                    "role '" + role.Id + "' may not be present at t=" + cue.T + " (no preceding actor.spawn and PresentAtStart=false)");
            return role;
        }

        private static void RequireLocation(CompileResult result, CapabilityManifest manifest, Cue cue, string locationId, string field, bool allowCurrent = false)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                Error(result, EUnknownLocation, cue.SourceIndex, cue.Type + " requires " + field);
                return;
            }
            if (allowCurrent && locationId == CurrentKeyword) return;
            if (manifest.FindLocation(locationId) == null)
                Error(result, EUnknownLocation, cue.SourceIndex, "unknown location '" + locationId + "' in " + field);
        }

        private static readonly Comparison<Cue> StableByTime = (a, b) =>
        {
            int c = a.T.CompareTo(b.T);
            if (c != 0) return c;
            // Deterministic tie-break: authored order first, injected restore cues last.
            if (a.Injected != b.Injected) return a.Injected ? 1 : -1;
            return a.SourceIndex.CompareTo(b.SourceIndex);
        };

        private static void Error(CompileResult r, string code, int cueIndex, string msg) =>
            r.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Code = code, CueIndex = cueIndex, Message = msg });

        private static void Warn(CompileResult r, string code, int cueIndex, string msg) =>
            r.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Code = code, CueIndex = cueIndex, Message = msg });
    }
}
