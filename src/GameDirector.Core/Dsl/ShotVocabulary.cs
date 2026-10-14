using System;
using System.Collections.Generic;
using System.Linq;

namespace GameDirector.Core.Dsl
{
    /// <summary>Semantics of one camera move: whether an authored shot of this
    /// type must name a destination anchor (shot.to). Motion evaluation itself
    /// lives in the engine; this is the shared vocabulary the compiler, the
    /// adapter preflight and the rig all consult instead of restating type lists.</summary>
    public sealed class ShotBehavior
    {
        public ShotBehavior(string id, bool requiresTarget, string motion) { Id = id; RequiresTarget = requiresTarget; Motion = motion; }
        public string Id { get; }
        public bool RequiresTarget { get; }
        public string Motion { get; }
    }

    /// <summary>One framing size and how strongly it fills the frame. Coverage is
    /// the subject-height fraction of frame height used by auto-framing only;
    /// explicit anchors keep their authored composition. A shot may override per
    /// take with the numeric param "coverage".</summary>
    public sealed class FrameBehavior
    {
        public FrameBehavior(string id, double coverage) { Id = id; Coverage = coverage; }
        public string Id { get; }
        public double Coverage { get; }
    }

    /// <summary>One authored camera parameter: name, default, unit and the
    /// screen-space meaning every engine must implement. The Unity
    /// CinematicCameraRig is the canonical behaviour this table records; other
    /// engines align to it instead of restating their own interpretation.</summary>
    public sealed class ShotParam
    {
        public ShotParam(string name, float? @default, string unit, string meaning, string derivation = null) { Name = name; Default = @default; Unit = unit; Meaning = meaning; Derivation = derivation; }
        public string Name { get; }
        /// <summary>Value when the shot omits the parameter; null = derived at
        /// shot start (subject bounds, frame coverage or start fov).</summary>
        public float? Default { get; }
        public string Unit { get; }
        /// <summary>One-line screen-space contract; signs are viewer-relative.</summary>
        public string Meaning { get; }
        public string Derivation { get; }
    }

    public static class ShotVocabulary
    {
        /// <summary>Reserved shot anchor: "wherever the camera is now". Never a
        /// manifest location id; compiler, rigs and adapters all use this constant.</summary>
        public const string CurrentAnchor = "current";
        public const float DefaultFov = 50f;
        /// <summary>Full-frame sensor height used for focalLengthMm ↔ fov.</summary>
        public const float SensorSizeMm = 24f;
        public const float MinFov = 5f, MaxFov = 170f;
        public const string FallbackFrameId = "wide";

        private static readonly ShotBehavior[] ShotTable =
        {
            new ShotBehavior("lockoff", false, MotionFormulas.Hold),
            new ShotBehavior("dolly", true, MotionFormulas.Lerp),
            new ShotBehavior("crane", true, MotionFormulas.Lerp),
            new ShotBehavior("orbit", false, MotionFormulas.OrbitUp),
            new ShotBehavior("tracking", false, MotionFormulas.TrackOffset),
        };

        private static readonly FrameBehavior[] FrameTable =
        {
            new FrameBehavior("extreme-closeup", 2.8),
            new FrameBehavior("closeup", 1.5),
            new FrameBehavior("medium", 1.05),
            new FrameBehavior("full", 0.8),
            new FrameBehavior("wide", 0.4),
        };

        private static readonly EaseSpec[] EaseTable = {
            new EaseSpec("linear", EaseFormulas.Identity, "p", new EaseSample(0,0), new EaseSample(.25f,.25f), new EaseSample(.5f,.5f), new EaseSample(.75f,.75f), new EaseSample(1,1)),
            new EaseSpec("in", EaseFormulas.QuadIn, "p*p", new EaseSample(0,0), new EaseSample(.25f,.0625f), new EaseSample(.5f,.25f), new EaseSample(.75f,.5625f), new EaseSample(1,1)),
            new EaseSpec("out", EaseFormulas.QuadOut, "1-(1-p)*(1-p)", new EaseSample(0,0), new EaseSample(.25f,.4375f), new EaseSample(.5f,.75f), new EaseSample(.75f,.9375f), new EaseSample(1,1)),
            new EaseSpec("inOut", EaseFormulas.Smoothstep, "p*p*(3-2*p)", new EaseSample(0,0), new EaseSample(.25f,.15625f), new EaseSample(.5f,.5f), new EaseSample(.75f,.84375f), new EaseSample(1,1))
        };
        public static readonly IReadOnlyList<string> Eases = Array.ConvertAll(EaseTable,e=>e.Id);
        public static IReadOnlyList<EaseSpec> EaseSpecs => EaseTable;

        private static readonly ShotParam[] ParamTable =
        {
            new ShotParam("targetHeight", null, "m", "Authored aim height above the subject pivot: replaces the bounds-center offset with (0,targetHeight,0); omitted = full vector (subject bounds center - subject pivot) at shot start.", ParamDerivations.SubjectBoundsCenter),
            new ShotParam("coverage", null, "subject-heights", "Auto-framing fill: subject height as a fraction of frame height (from:current only); defaults to the frame size's coverage.", ParamDerivations.FrameCoverage),
            new ShotParam("distance", 0, "m", "Camera distance from the subject along the current view direction; 0 = derive from frame coverage."),
            new ShotParam("offsetX", 0, "m", "World-axis offset added to the resolved start position."),
            new ShotParam("offsetY", 0, "m", "World-axis offset added to the resolved start position."),
            new ShotParam("offsetZ", 0, "m", "World-axis offset added to the resolved start position."),
            new ShotParam("toOffsetX", 0, "m", "World-axis offset added to the resolved destination of a moving shot."),
            new ShotParam("toOffsetY", 0, "m", "World-axis offset added to the resolved destination of a moving shot."),
            new ShotParam("toOffsetZ", 0, "m", "World-axis offset added to the resolved destination of a moving shot."),
            new ShotParam("orbitDeg", 90, "deg", "Total orbit sweep across the shot duration; + carries the camera toward its own left (clockwise seen from above)."),
            new ShotParam("shake", 0, "m", "Handheld vertical shake; amplitude ~value x 0.025 m; 0 = locked off."),
            new ShotParam("tilt", 0, "deg", "Pitch applied after aiming; + looks down."),
            new ShotParam("pan", 0, "deg", "Yaw applied after aiming; + turns right."),
            new ShotParam("roll", 0, "deg", "Roll around the view axis applied after aiming; applied after pan and tilt; + turns screen content clockwise (pinned by camera.aim.samples)."),
            new ShotParam("endFov", null, "deg", "Fov at shot end; ramps from the start fov across the shot.", ParamDerivations.StartFov),
        };
        private static readonly Dictionary<string,ShotParam> ParamIndex = ParamTable.ToDictionary(p=>p.Name,StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string,string> DerivationTable = new Dictionary<string,string> {
            {ParamDerivations.SubjectBoundsCenter,"when targetHeight is omitted, aim offset = full vector (subject bounds center - subject pivot) at shot start, preserving X, Y and Z; when authored, aim offset = (0,targetHeight,0)"},
            {ParamDerivations.FrameCoverage,"coverage = Coverage(frame) at shot start"},
            {ParamDerivations.StartFov,"endFov = start fov"}
        };
        public static IReadOnlyDictionary<string,string> Derivations => DerivationTable;

        /// <summary>Canonically safe choices for generated first drafts.</summary>
        public const string DefaultShotId = "lockoff";
        public const string DefaultFrameId = "full";

        /// <summary>What an authored shot means when it omits the field (ShotSpec
        /// initializers). Distinct from DefaultShotId/DefaultFrameId above, which
        /// generators write into first drafts; these fill silence in an authored file.</summary>
        public const string OmittedShotId = "lockoff";
        public const string OmittedFrameId = "medium";
        public const string OmittedEase = "inOut";

        public static IReadOnlyList<ShotBehavior> Shots => ShotTable;
        public static IReadOnlyList<FrameBehavior> Frames => FrameTable;
        /// <summary>The built-in shot parameter table. A built-in name means the
        /// same thing in every engine: an adapter whose camera.params lists a
        /// built-in name implements the built-in semantics recorded here.</summary>
        public static IReadOnlyList<ShotParam> Params => ParamTable;
        public static List<string> ShotIds => ShotTable.Select(s => s.Id).ToList();
        public static List<string> FrameIds => FrameTable.Select(f => f.Id).ToList();

        public static ShotBehavior FindShot(string id) =>
            id == null ? null : ShotTable.FirstOrDefault(s => s.Id == id);
        public static FrameBehavior FindFrame(string id) =>
            id == null ? null : FrameTable.FirstOrDefault(f => f.Id == id);
        public static ShotParam FindParam(string name) => name != null && ParamIndex.TryGetValue(name,out var p) ? p : null;
        public static float DefaultOf(string name) => name != null && ParamIndex.TryGetValue(name,out var p) && p.Default.HasValue ? p.Default.Value
            : throw new ArgumentException("shot param '"+name+"' has no contract default (unknown or derived)",nameof(name));
        public static EaseSpec FindEase(string id) => id == null ? null : Array.Find(EaseTable,e=>e.Id==id);

        /// <summary>True/false for built-in types; null for manifest-declared
        /// custom types, whose semantics belong to the registering engine.</summary>
        public static bool? RequiresTarget(string shotType) => FindShot(shotType)?.RequiresTarget;

        /// <summary>Auto-framing coverage for a known frame size. Unknown/custom
        /// sizes fall back to the widest built-in coverage.</summary>
        public static double Coverage(string frameId) => (FindFrame(frameId) ?? FindFrame(FallbackFrameId)).Coverage;

        public static bool ValidEase(string ease) => FindEase(ease) != null;
    }
}
