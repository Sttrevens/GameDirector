using System;
using System.Collections.Generic;

namespace GameDirector.Core.Dsl
{
    /// <summary>
    /// Well-known cue type identifiers for DSL v0.1.
    /// A cue type is a stable contract: once shipped, its meaning may only
    /// be extended additively. New semantics require a new cue type.
    /// </summary>
    public static class CueTypes
    {
        public const string CameraShot = "camera.shot";       // start a camera shot (Shot payload)
        public const string ActorSpawn = "actor.spawn";       // bring a role into the scene (Location, HeadingTo)
        public const string ActorDespawn = "actor.despawn";   // remove a role from the scene (Role)
        public const string ActorAnim = "actor.anim";         // play an animation clip (Role, Clip, Fade)
        public const string ActorMove = "actor.move";         // move a role toward a location (Role, To, Speed, HeadingTo)
        public const string ActorFace = "actor.face";         // rotate a role toward a role or location (Role, HeadingTo)
        public const string AudioPlay = "audio.play";         // play an audio id from the manifest (AudioId, Volume)
        public const string AudioStop = "audio.stop";         // stop an audio id (AudioId)
        public const string WorldTimeScale = "world.timescale"; // set time scale; Duration>0 auto-restores to 1 (Scale, Duration)
        public const string Marker = "marker";                // pure timeline annotation / edit beat (Label)

        public static readonly string[] All =
        {
            CameraShot, ActorSpawn, ActorDespawn, ActorAnim, ActorMove, ActorFace,
            AudioPlay, AudioStop, WorldTimeScale, Marker
        };
    }

    /// <summary>
    /// One timed instruction. Flat POCO on purpose: the same shape serializes
    /// with both System.Text.Json (CLI/MCP) and Newtonsoft.Json (Unity bridge),
    /// with zero attributes so neither serializer is confused.
    /// Unknown fields are ignored by readers; unknown cue types are compile errors.
    /// </summary>
    public sealed class Cue
    {
        /// <summary>Seconds from timeline start. Must be finite and &gt;= 0.</summary>
        public double T { get; set; }

        /// <summary>One of <see cref="CueTypes"/>.</summary>
        public string Type { get; set; }

        // Actor payloads
        public string Role { get; set; }
        public string Clip { get; set; }
        public float Fade { get; set; } = 0.2f;
        public string Location { get; set; }
        public string To { get; set; }
        public float Speed { get; set; } = 3f;
        public string HeadingTo { get; set; }

        // Camera payload
        public ShotSpec Shot { get; set; }

        // Audio payloads
        public string AudioId { get; set; }
        public float Volume { get; set; } = 1f;

        // World payloads
        public float Scale { get; set; } = 1f;
        public double Duration { get; set; }

        // Marker payload
        public string Label { get; set; }

        // ---- compiler bookkeeping (not authored) ----
        /// <summary>Index of this cue in the authored file; -1 when injected by the compiler.</summary>
        public int SourceIndex { get; set; } = -1;
        /// <summary>True when the compiler generated this cue (e.g. timescale auto-restore).</summary>
        public bool Injected { get; set; }
    }

    /// <summary>
    /// Camera shot description. Positions are expressed via manifest location ids
    /// (or the reserved keyword "current"), subjects via role ids. The adapter
    /// turns this into concrete engine camera motion; the kernel never sees a Transform.
    /// </summary>
    public sealed class ShotSpec
    {
        /// <summary>lockoff | dolly | orbit | tracking | crane — must exist in manifest ShotTypes.</summary>
        public string Type { get; set; } = "lockoff";

        /// <summary>Role the shot frames (and looks at, unless LookAt overrides).</summary>
        public string Subject { get; set; }

        /// <summary>extreme-closeup | closeup | medium | full | wide — must exist in manifest FrameTypes.</summary>
        public string Frame { get; set; } = "medium";

        /// <summary>Start location id, or "current".</summary>
        public string From { get; set; } = "current";

        /// <summary>End location id for moving shots (dolly/crane), or "current".</summary>
        public string To { get; set; }

        /// <summary>Shot length in seconds. 0 = cut/hold until next shot cue.</summary>
        public double DurationSeconds { get; set; }

        public float? Fov { get; set; }
        public float? FocalLengthMm { get; set; }

        /// <summary>linear | in | out | inOut</summary>
        public string Ease { get; set; } = "inOut";

        /// <summary>Optional different look-at role.</summary>
        public string LookAt { get; set; }

        /// <summary>Open numeric parameters for adapter-specific flavor (shake, roll, ...).</summary>
        public Dictionary<string, float> Params { get; set; }
    }

    /// <summary>
    /// Authored timeline asset. This is the artifact LLMs write, humans review,
    /// and the compiler validates. Same file must produce the same events forever.
    /// </summary>
    public sealed class TimelineAsset
    {
        public string Version { get; set; } = "0.1";
        public string Id { get; set; }
        public string Title { get; set; }
        public List<Cue> Cues { get; set; } = new List<Cue>();
        public Dictionary<string, string> Metadata { get; set; }
    }

    /// <summary>Compiler output: validated, deterministically ordered, ready to play.</summary>
    public sealed class CompiledTimeline
    {
        public string Id { get; set; }
        public string Title { get; set; }

        /// <summary>Stable-sorted by T; includes injected cues (e.g. timescale restore).</summary>
        public IReadOnlyList<Cue> OrderedCues { get; set; }

        /// <summary>End of the timeline in seconds (last cue T, extended by trailing shot duration).</summary>
        public double Duration { get; set; }
    }
}
