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
        public ShotBehavior(string id, bool requiresTarget) { Id = id; RequiresTarget = requiresTarget; }
        public string Id { get; }
        public bool RequiresTarget { get; }
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

    public static class ShotVocabulary
    {
        public const float DefaultFov = 50f;
        /// <summary>Full-frame sensor height used for focalLengthMm ↔ fov.</summary>
        public const float SensorSizeMm = 24f;

        private static readonly ShotBehavior[] ShotTable =
        {
            new ShotBehavior("lockoff", requiresTarget: false),
            new ShotBehavior("dolly", requiresTarget: true),
            new ShotBehavior("crane", requiresTarget: true),
            new ShotBehavior("orbit", requiresTarget: false),
            new ShotBehavior("tracking", requiresTarget: false),
        };

        private static readonly FrameBehavior[] FrameTable =
        {
            new FrameBehavior("extreme-closeup", 2.8),
            new FrameBehavior("closeup", 1.5),
            new FrameBehavior("medium", 1.05),
            new FrameBehavior("full", 0.8),
            new FrameBehavior("wide", 0.4),
        };

        public static readonly IReadOnlyList<string> Eases = new[] { "linear", "in", "out", "inOut" };

        /// <summary>Canonically safe choices for generated first drafts.</summary>
        public const string DefaultShotId = "lockoff";
        public const string DefaultFrameId = "full";

        public static IReadOnlyList<ShotBehavior> Shots => ShotTable;
        public static IReadOnlyList<FrameBehavior> Frames => FrameTable;
        public static List<string> ShotIds => ShotTable.Select(s => s.Id).ToList();
        public static List<string> FrameIds => FrameTable.Select(f => f.Id).ToList();

        public static ShotBehavior FindShot(string id) =>
            id == null ? null : ShotTable.FirstOrDefault(s => s.Id == id);
        public static FrameBehavior FindFrame(string id) =>
            id == null ? null : FrameTable.FirstOrDefault(f => f.Id == id);

        /// <summary>True/false for built-in types; null for manifest-declared
        /// custom types, whose semantics belong to the registering engine.</summary>
        public static bool? RequiresTarget(string shotType) => FindShot(shotType)?.RequiresTarget;

        /// <summary>Auto-framing coverage for a known frame size. Unknown/custom
        /// sizes fall back to the widest built-in coverage.</summary>
        public static double Coverage(string frameId) => FindFrame(frameId)?.Coverage ?? FrameTable[FrameTable.Length - 1].Coverage;

        public static bool ValidEase(string ease) => ease != null && Eases.Contains(ease);
    }
}
