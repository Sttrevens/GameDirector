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
        /// <summary>Authored-document coordinate of the offending field, e.g.
        /// "cues[3].shot.to" or "manifest.capabilities.camera.params"; "" = asset level.
        /// Readers feed this back to the author, who never sees a CueIndex.</summary>
        public string Path = "";

        public override string ToString()
        {
            string where = !string.IsNullOrEmpty(Path) ? Path : CueIndex >= 0 ? "cue[" + CueIndex + "]" : "asset";
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
        public const string ERoleNotPresent = "GD1011";
        public const string EUnsupportedByAdapter = "GD1012";
        public const string EAspectHint = "GD1013";
        public const string WShotOverlap = "GD2001";
        // GD2002 (role-presence warning) is retired: every executing host rejected
        // such timelines anyway, so presence is the GD1011 error above.
        public const string WEmptyLabel = "GD2003";

        public static CompileResult Compile(TimelineAsset asset, CapabilityManifest manifest, double? aspect = null)
        {
            var result = new CompileResult();
            if (asset == null) { Error(result, EBadValue, -1, "timeline asset is null"); return result; }
            if (manifest == null) { Error(result, EBadValue, -1, "capability manifest is null"); return result; }

            if (asset.Version != SupportedVersion)
                Error(result, EUnsupportedVersion, -1, "version '" + asset.Version + "' is not supported (expected " + SupportedVersion + ")", "version");
            if (string.IsNullOrEmpty(asset.Id))
                Error(result, EMissingId, -1, "timeline id is required", "id");

            result.Diagnostics.AddRange(ValidateManifest(manifest).Diagnostics);
            if (result.HasErrors) return result;
            AspectHints.Validate(asset, aspect, result);
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
                if (c == null) { Error(result, EBadValue, i, "null cue", "cues[" + i + "]"); continue; }
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
                    Error(result, EBadTime, cue.SourceIndex, "cue time must be finite and >= 0, got " + cue.T, Field(cue, "t"));
                    continue;
                }
                bool kept = cue.Aspect == null || AspectHints.Matches(cue, aspect.Value);
                if (kept && cue.T > duration) duration = cue.T;

                ApplyCueRules(result, manifest, cue);
                switch (cue.Type)
                {
                    case CueTypes.CameraShot:
                        ValidateShot(result, manifest, cue);
                        if (cue.Shot != null) {
                            // Presence is checked once per named role; an omitted
                            // subject already failed inside ValidateShot.
                            if (!string.IsNullOrEmpty(cue.Shot.Subject)) RequireRolePresent(result, manifest, cue, cue.Shot.Subject, present, "shot.subject");
                            if (cue.Shot.LookAt != null) RequireRolePresent(result, manifest, cue, cue.Shot.LookAt, present, "shot.lookAt");
                            if (cue.Shot.With != null) for (int i = 0; i < cue.Shot.With.Count; i++)
                                RequireRoleDuringShot(result, manifest, cue, cue.Shot.With[i], present, indexed, "shot.with[" + i + "]");
                        }
                        if (!kept) continue; // all alternates validated, only the selected one owns a window
                        if (openShotEnd > cue.T + FrameClock.CueEpsilonSeconds)
                            Warn(result, WShotOverlap, cue.SourceIndex,
                                "camera shot at t=" + cue.T + " cuts into the shot started at t=" + openShotT + " (window ends at " + openShotEnd + "; later shot wins)", Field(cue, "shot"));
                        openShotT = cue.T;
                        // durationSeconds=0 means "hold until the next shot": replaced, not overlapped.
                        openShotEnd = cue.Shot != null && cue.Shot.DurationSeconds > 0
                            ? cue.T + cue.Shot.DurationSeconds
                            : -1;
                        if (openShotEnd > duration) duration = openShotEnd;
                        break;

                    case CueTypes.ActorSpawn:
                        RequireRole(result, manifest, cue, cue.Role, "role");
                        RequireLocation(result, manifest, cue, cue.Location, "location");
                        if (cue.Role != null && present.TryGetValue(cue.Role, out var already) && already)
                            Error(result, EBadValue, cue.SourceIndex, "role is already present; despawn before spawn", Field(cue, "role"));
                        ValidateHeading(result, manifest, cue, false, present);
                        if (cue.Role != null && present.ContainsKey(cue.Role)) present[cue.Role] = true;
                        break;

                    case CueTypes.ActorDespawn:
                        RequireRolePresent(result, manifest, cue, cue.Role, present, "role");
                        if (cue.Role != null && present.ContainsKey(cue.Role)) present[cue.Role] = false;
                        break;

                    case CueTypes.ActorAnim:
                        ValidateAnim(result, manifest, cue, present);
                        break;

                    case CueTypes.ActorMove:
                        RequireRolePresent(result, manifest, cue, cue.Role, present, "role");
                        RequireLocation(result, manifest, cue, cue.To, "to");
                        if (!Finite(cue.Speed) || cue.Speed <= 0) Error(result, EBadValue, cue.SourceIndex, "actor.move Speed must be > 0", Field(cue, "speed"));
                        ValidateHeading(result, manifest, cue, false, present);
                        break;

                    case CueTypes.ActorFace:
                        RequireRolePresent(result, manifest, cue, cue.Role, present, "role");
                        ValidateHeading(result, manifest, cue, true, present);
                        break;

                    case CueTypes.AudioPlay:
                        if (manifest.FindAudio(cue.AudioId) == null)
                            Error(result, EUnknownAudio, cue.SourceIndex, "unknown audio id '" + cue.AudioId + "'", Field(cue, "audioId"));
                        if (!Finite(cue.Volume) || cue.Volume < 0f || cue.Volume > 1f)
                            Error(result, EBadValue, cue.SourceIndex, "audio.play Volume must be in [0,1]", Field(cue, "volume"));
                        break;

                    case CueTypes.AudioStop:
                        if (manifest.FindAudio(cue.AudioId) == null)
                            Error(result, EUnknownAudio, cue.SourceIndex, "unknown audio id '" + cue.AudioId + "'", Field(cue, "audioId"));
                        break;

                    case CueTypes.WorldTimeScale:
                        if (!Finite(cue.Scale) || cue.Scale <= 0f || cue.Scale > 10f) Error(result, EBadValue, cue.SourceIndex, "world.timescale Scale must be > 0 and <= 10", Field(cue, "scale"));
                        if (!Finite(cue.Duration) || cue.Duration < 0 || !Finite(cue.T + cue.Duration)) Error(result, EBadValue, cue.SourceIndex, "world.timescale Duration must be >= 0", Field(cue, "duration"));
                        break;

                    case CueTypes.Marker:
                        if (string.IsNullOrEmpty(cue.Label))
                            Warn(result, WEmptyLabel, cue.SourceIndex, "marker without label", Field(cue, "label"));
                        break;

                    default:
                        Error(result, EUnknownCueType, cue.SourceIndex, "unknown cue type '" + cue.Type + "'", Field(cue, "type"));
                        break;
                }

                cue.Aspect = null;
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
                Error(result, EBadValue, cue.SourceIndex, "camera.shot requires a shot payload", Field(cue, "shot"));
                return;
            }
            var s = cue.Shot;

            if (!manifest.ShotTypes.Contains(s.Type))
                Error(result, EUnknownShotType, cue.SourceIndex, "unknown shot type '" + s.Type + "' (manifest declares: " + string.Join("/", manifest.ShotTypes.ToArray()) + ")", Field(cue, "shot.type"));
            if (!manifest.FrameTypes.Contains(s.Frame))
                Error(result, EUnknownFrameType, cue.SourceIndex, "unknown frame type '" + s.Frame + "' (manifest declares: " + string.Join("/", manifest.FrameTypes.ToArray()) + ")", Field(cue, "shot.frame"));

            if (string.IsNullOrEmpty(s.Subject))
                Error(result, EUnknownRole, cue.SourceIndex, "camera.shot requires a subject role", Field(cue, "shot.subject"));
            else if (manifest.FindRole(s.Subject) == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown subject role '" + s.Subject + "'", Field(cue, "shot.subject"));

            if (s.With != null) {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < s.With.Count; i++) if (s.With[i] == s.Subject || !seen.Add(s.With[i]))
                    Error(result, EBadValue, cue.SourceIndex, "with ids must be distinct and must not name the subject", Field(cue, "shot.with[" + i + "]"));
            }
            if (s.LookAt != null && manifest.FindRole(s.LookAt) == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown lookAt role '" + s.LookAt + "'", Field(cue, "shot.lookAt"));

            RequireLocation(result, manifest, cue, s.From, "shot.from", allowCurrent: true);
            // A written destination must resolve for every shot type;
            // RequiresTarget only decides whether omitting it is an error.
            if (ShotVocabulary.RequiresTarget(s.Type) == true || s.To != null)
                RequireLocation(result, manifest, cue, s.To, "shot.to", allowCurrent: true);

            if (!Finite(s.DurationSeconds) || s.DurationSeconds < 0 || !Finite(cue.T + s.DurationSeconds))
                Error(result, EBadValue, cue.SourceIndex, "shot durationSeconds must be >= 0", Field(cue, "shot.durationSeconds"));
            if (s.Fov.HasValue && (!Finite(s.Fov.Value) || s.Fov.Value < ShotVocabulary.MinFov || s.Fov.Value > ShotVocabulary.MaxFov))
                Error(result, EBadValue, cue.SourceIndex, "shot fov out of range [" + ShotVocabulary.MinFov + "," + ShotVocabulary.MaxFov + "]: " + s.Fov.Value, Field(cue, "shot.fov"));
            if (s.FocalLengthMm.HasValue && (!Finite(s.FocalLengthMm.Value) || s.FocalLengthMm.Value <= 0))
                Error(result, EBadValue, cue.SourceIndex, "focalLengthMm must be finite and positive", Field(cue, "shot.focalLengthMm"));
            if (s.Fov.HasValue && s.FocalLengthMm.HasValue)
                Error(result, EBadValue, cue.SourceIndex, "choose fov or focalLengthMm, not both", Field(cue, "shot.fov"));
            if (!ShotVocabulary.ValidEase(s.Ease))
                Error(result, EBadValue, cue.SourceIndex, "unknown easing", Field(cue, "shot.ease"));
            var paramSpec = CapabilitySpecs.Find(CapabilityKeys.CameraParams);
            CapabilityRule paramRule = null;
            var declared = CapabilitySpecs.Declared(manifest, paramSpec.Key);
            foreach (var rule in paramSpec.Rules) if (rule.Effect == CapabilityEffects.RestrictShotParams && rule.CueType == cue.Type && (rule.When == null || rule.When == declared)) paramRule = rule;
            // Parse the complete declared set once per shot, never once per param.
            var allowed = declared == null ? null : new HashSet<string>(paramSpec.Items(declared), StringComparer.Ordinal);
            if (s.Params != null) foreach (var pair in s.Params)
            {
                if (!Finite(pair.Value)) { Error(result, EBadValue, cue.SourceIndex, "shot parameters must be finite", Field(cue, "shot.params")); continue; }
                if (paramRule == null) continue;
                if (allowed != null)
                {
                    if (!allowed.Contains(pair.Key))
                        Error(result, EUnsupportedByAdapter, cue.SourceIndex, "shot parameter '" + pair.Key + "' is not in this adapter's camera.params (" + declared + ")", Field(cue, paramRule.Field + "." + pair.Key));
                }
                else if (paramSpec.Undeclared == CapabilityUndeclared.BuiltinTable && ShotVocabulary.FindParam(pair.Key) == null)
                    Error(result, EUnsupportedByAdapter, cue.SourceIndex, "unknown shot parameter '" + pair.Key + "'; built-in parameters: " + BuiltinParamNames(), Field(cue, paramRule.Field + "." + pair.Key));
            }
        }

        private static void ApplyCueRules(CompileResult result, CapabilityManifest manifest, Cue cue)
        {
            foreach (var spec in CapabilitySpecs.All)
            {
                var value = CapabilitySpecs.Declared(manifest, spec.Key);
                if (value == null) continue;
                foreach (var rule in spec.Rules)
                {
                    if (rule.CueType != cue.Type || (rule.When != null && rule.When != value)) continue;
                    bool refuses = rule.Effect == CapabilityEffects.ForbidCue;
                    if (rule.Effect == CapabilityEffects.RequireZero) {
                        var number = CapabilitySpecs.NumericField(cue, rule.Field);
                        refuses = number.HasValue && Finite(number.Value) && number.Value > 0;
                    }
                    if (refuses) Error(result, EUnsupportedByAdapter, cue.SourceIndex, "this adapter declares " + spec.Key + ": " + value + "; " + rule.Remedy, Field(cue, rule.Field));
                }
            }
        }

        private static string BuiltinParamNames()
        {
            var names = new List<string>();
            foreach (var p in ShotVocabulary.Params) names.Add(p.Name);
            return string.Join(", ", names.ToArray());
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void ValidateHeading(CompileResult r, CapabilityManifest m, Cue c, bool required, Dictionary<string, bool> present)
        {
            if (string.IsNullOrWhiteSpace(c.HeadingTo)) {
                if (required) Error(r, EBadValue, c.SourceIndex, "headingTo is required", Field(c, "headingTo"));
            } else if (m.FindRole(c.HeadingTo) == null && m.FindLocation(c.HeadingTo) == null)
                Error(r, EUnknownLocation, c.SourceIndex, "unknown headingTo '" + c.HeadingTo + "'", Field(c, "headingTo"));
            else if (m.FindRole(c.HeadingTo) != null) RequireRolePresent(r, m, c, c.HeadingTo, present, "headingTo");
        }

        /// <summary>Independent engine-manifest verdict. Its diagnostics are
        /// adapter/Core faults, never errors in the caller's timeline.</summary>
        public static CompileResult ValidateManifest(CapabilityManifest manifest)
        {
            var result = new CompileResult();
            if (manifest == null) Error(result, EBadValue, -1, "capability manifest is null", "manifest");
            else ValidateManifest(result, manifest);
            return result;
        }

        private static void ValidateManifest(CompileResult r, CapabilityManifest m)
        {
            if (m.ManifestVersion != SupportedVersion) Error(r, EUnsupportedVersion, -1, "unsupported manifest version", "manifest.manifestVersion");
            if (m.Roles == null || m.Actors == null || m.Locations == null || m.Audio == null || m.ShotTypes == null || m.FrameTypes == null) {
                Error(r, EBadValue, -1, "manifest collections must not be null", "manifest"); return;
            }
            ValidateIds(r, m.Roles.ConvertAll(x => x?.Id), "role", "manifest.roles");
            ValidateIds(r, m.Actors.ConvertAll(x => x?.Id), "actor", "manifest.actors");
            ValidateIds(r, m.Locations.ConvertAll(x => x?.Id), "location", "manifest.locations");
            ValidateIds(r, m.Audio.ConvertAll(x => x?.Id), "audio", "manifest.audio");
            ValidateIds(r, m.ShotTypes, "shot type", "manifest.shotTypes");
            ValidateIds(r, m.FrameTypes, "frame type", "manifest.frameTypes");
            if (r.HasErrors) return;
            foreach (var role in m.Roles)
                if (m.FindActor(role.DefaultActor) == null) Error(r, EBadValue, -1, "role '" + role.Id + "' has no declared actor", "manifest.roles");
            foreach (var a in m.Actors) {
                if (a.Clips == null) Error(r, EBadValue, -1, "clips must not be null", "manifest.actors");
                else ValidateIds(r, a.Clips, "clip on " + a.Id, "manifest.actors");
            }
            foreach (var loc in m.Locations) {
                if (loc.Id == ShotVocabulary.CurrentAnchor) Error(r, EBadValue, -1, "current is a reserved location", "manifest.locations");
                if (loc.Position != null && (loc.Position.Length != 3 || Array.Exists(loc.Position, v => !Finite(v))))
                    Error(r, EBadValue, -1, "location position must be three finite numbers", "manifest.locations");
                if (!Finite(loc.HeadingDeg)) Error(r, EBadValue, -1, "heading must be finite", "manifest.locations");
            }
            // Known keys are typed in table order; extension keys stay uninterpreted.
            if (m.Capabilities != null) foreach (var spec in CapabilitySpecs.All)
                if (m.Capabilities.TryGetValue(spec.Key, out var value)) foreach (var problem in spec.Problems(value))
                    Error(r, EBadValue, -1, problem, "manifest.capabilities." + spec.Key);
        }

        private static void ValidateIds(CompileResult r, List<string> ids, string kind, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids) if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                Error(r, EBadValue, -1, "empty or duplicate " + kind + " id", path);
        }

        private static void ValidateAnim(CompileResult result, CapabilityManifest manifest, Cue cue, Dictionary<string, bool> present)
        {
            if (!Finite(cue.Fade) || cue.Fade < 0) Error(result, EBadValue, cue.SourceIndex, "animation fade must be finite and >= 0 seconds", Field(cue, "fade"));
            var role = RequireRolePresent(result, manifest, cue, cue.Role, present, "role");
            if (role == null) return;
            if (string.IsNullOrEmpty(cue.Clip))
            {
                Error(result, EUnknownClip, cue.SourceIndex, "actor.anim requires a clip", Field(cue, "clip"));
                return;
            }
            var actor = manifest.FindActor(role.DefaultActor);
            if (actor == null || actor.Clips == null || !actor.Clips.Contains(cue.Clip))
                Error(result, EUnknownClip, cue.SourceIndex,
                    "clip '" + cue.Clip + "' is not declared on actor '" + role.DefaultActor + "'", Field(cue, "clip"));
        }

        private static RoleDescriptor RequireRole(CompileResult result, CapabilityManifest manifest, Cue cue, string roleId, string field)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                Error(result, EUnknownRole, cue.SourceIndex, cue.Type + " requires a role", Field(cue, field));
                return null;
            }
            var role = manifest.FindRole(roleId);
            if (role == null)
                Error(result, EUnknownRole, cue.SourceIndex, "unknown role '" + roleId + "'", Field(cue, field));
            return role;
        }

        private static RoleDescriptor RequireRolePresent(CompileResult result, CapabilityManifest manifest, Cue cue, string roleId, Dictionary<string, bool> present, string field)
        {
            var role = RequireRole(result, manifest, cue, roleId, field);
            if (role != null && present.TryGetValue(role.Id, out bool isPresent) && !isPresent)
                Error(result, ERoleNotPresent, cue.SourceIndex,
                    "role '" + role.Id + "' is not present at t=" + cue.T + " (no preceding actor.spawn and PresentAtStart=false)", Field(cue, field));
            return role;
        }

        // Vertical fit is review-only: a protected role may enter after shot start.
        // Windows end at the next camera cut, or at durationSeconds; zero holds to the next cut/end.
        private static void RequireRoleDuringShot(CompileResult result, CapabilityManifest manifest, Cue cue, string roleId, Dictionary<string, bool> present, List<Cue> indexed, string field)
        {
            var role = RequireRole(result, manifest, cue, roleId, field);
            if (role == null || present[role.Id]) return;
            double end = cue.Shot.DurationSeconds > 0 ? cue.T + cue.Shot.DurationSeconds : double.PositiveInfinity;
            foreach (var next in indexed) if (next.Type == CueTypes.CameraShot && next.T > cue.T && next.T < end) end = next.T;
            bool enters = indexed.Exists(c => c.Type == CueTypes.ActorSpawn && c.Role == roleId && c.T < end &&
                (c.T > cue.T || c.T == cue.T && c.SourceIndex > cue.SourceIndex));
            if (!enters) Error(result, ERoleNotPresent, cue.SourceIndex, "role '" + roleId + "' is never present during the shot window", Field(cue, field));
        }

        private static void RequireLocation(CompileResult result, CapabilityManifest manifest, Cue cue, string locationId, string field, bool allowCurrent = false)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                Error(result, EUnknownLocation, cue.SourceIndex, cue.Type + " requires " + field, Field(cue, field));
                return;
            }
            if (allowCurrent && locationId == ShotVocabulary.CurrentAnchor) return;
            if (manifest.FindLocation(locationId) == null)
                Error(result, EUnknownLocation, cue.SourceIndex, "unknown location '" + locationId + "' in " + field, Field(cue, field));
        }

        private static readonly Comparison<Cue> StableByTime = (a, b) =>
        {
            int c = a.T.CompareTo(b.T);
            if (c != 0) return c;
            // Deterministic tie-break: authored order first, injected restore cues last.
            if (a.Injected != b.Injected) return a.Injected ? 1 : -1;
            return a.SourceIndex.CompareTo(b.SourceIndex);
        };

        // Authored-document coordinate of one cue field, e.g. "cues[3].shot.to".
        private static string Field(Cue cue, string name) => "cues[" + cue.SourceIndex + "]." + name;

        private static void Error(CompileResult r, string code, int cueIndex, string msg, string path = "") =>
            r.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Code = code, CueIndex = cueIndex, Path = path ?? "", Message = msg });

        private static void Warn(CompileResult r, string code, int cueIndex, string msg, string path = "") =>
            r.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Code = code, CueIndex = cueIndex, Path = path ?? "", Message = msg });
    }
}
