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
                        if (cue.Role != null && present.ContainsKey(cue.Role)) present[cue.Role] = true;
                        break;

                    case CueTypes.ActorDespawn:
                        RequireRole(result, manifest, cue, cue.Role);
                        if (cue.Role != null && present.ContainsKey(cue.Role)) present[cue.Role] = false;
                        break;

                    case CueTypes.ActorAnim:
                        ValidateAnim(result, manifest, cue, present);
                        break;

                    case CueTypes.ActorMove:
                        RequireRolePresent(result, manifest, cue, cue.Role, present);
                        RequireLocation(result, manifest, cue, cue.To, "To");
                        if (cue.Speed <= 0) Error(result, EBadValue, cue.SourceIndex, "actor.move Speed must be > 0");
                        break;

                    case CueTypes.ActorFace:
                        RequireRolePresent(result, manifest, cue, cue.Role, present);
                        if (cue.HeadingTo != null
                            && manifest.FindRole(cue.HeadingTo) == null
                            && manifest.FindLocation(cue.HeadingTo) == null)
                            Error(result, EUnknownRole, cue.SourceIndex,
                                "actor.face HeadingTo '" + cue.HeadingTo + "' is neither a role nor a location");
                        break;

                    case CueTypes.AudioPlay:
                        if (manifest.FindAudio(cue.AudioId) == null)
                            Error(result, EUnknownAudio, cue.SourceIndex, "unknown audio id '" + cue.AudioId + "'");
                        if (cue.Volume < 0f || cue.Volume > 1f)
                            Error(result, EBadValue, cue.SourceIndex, "audio.play Volume must be in [0,1]");
                        break;

                    case CueTypes.AudioStop:
                        if (manifest.FindAudio(cue.AudioId) == null)
                            Error(result, EUnknownAudio, cue.SourceIndex, "unknown audio id '" + cue.AudioId + "'");
                        break;

                    case CueTypes.WorldTimeScale:
                        if (cue.Scale <= 0f) Error(result, EBadValue, cue.SourceIndex, "world.timescale Scale must be > 0");
                        if (cue.Duration < 0) Error(result, EBadValue, cue.SourceIndex, "world.timescale Duration must be >= 0");
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
                    compiled.Add(restore);
                    if (restore.T > duration) duration = restore.T;
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

            if (manifest.ShotTypes != null && manifest.ShotTypes.Count > 0 && !manifest.ShotTypes.Contains(s.Type))
                Error(result, EUnknownShotType, cue.SourceIndex, "unknown shot type '" + s.Type + "' (manifest declares: " + string.Join("/", manifest.ShotTypes.ToArray()) + ")");
            if (manifest.FrameTypes != null && manifest.FrameTypes.Count > 0 && !manifest.FrameTypes.Contains(s.Frame))
                Error(result, EUnknownFrameType, cue.SourceIndex, "unknown frame type '" + s.Frame + "' (manifest declares: " + string.Join("/", manifest.FrameTypes.ToArray()) + ")");

            if (string.IsNullOrEmpty(s.Subject))
                Error(result, EUnknownRole, cue.SourceIndex, "camera.shot requires a subject role");
            else if (manifest.FindRole(s.Subject) == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown subject role '" + s.Subject + "'");

            if (s.LookAt != null && manifest.FindRole(s.LookAt) == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown lookAt role '" + s.LookAt + "'");

            RequireLocation(result, manifest, cue, s.From, "shot.from", allowCurrent: true);
            if (s.Type != "lockoff" && s.Type != "orbit" && s.Type != "tracking")
                RequireLocation(result, manifest, cue, s.To, "shot.to", allowCurrent: true);

            if (s.DurationSeconds < 0)
                Error(result, EBadValue, cue.SourceIndex, "shot durationSeconds must be >= 0");
            if (s.Fov.HasValue && (s.Fov.Value < 5f || s.Fov.Value > 170f))
                Error(result, EBadValue, cue.SourceIndex, "shot fov out of range [5,170]: " + s.Fov.Value);
        }

        private static void ValidateAnim(CompileResult result, CapabilityManifest manifest, Cue cue, Dictionary<string, bool> present)
        {
            var role = RequireRolePresent(result, manifest, cue, cue.Role, present);
            if (role == null) return;
            if (string.IsNullOrEmpty(cue.Clip))
            {
                Error(result, EUnknownClip, cue.SourceIndex, "actor.anim requires a clip");
                return;
            }
            var actor = manifest.FindActor(role.DefaultActor);
            if (actor != null && actor.Clips != null && actor.Clips.Count > 0 && !actor.Clips.Contains(cue.Clip))
                Error(result, EUnknownClip, cue.SourceIndex,
                    "clip '" + cue.Clip + "' is not declared on actor '" + actor.Id + "' (declares " + actor.Clips.Count + " clips)");
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
                if (!allowCurrent)
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
