using System.Collections.Generic;
using GameDirector.Core.Dsl;

namespace GameDirector.Core.Adapters
{
    /// <summary>
    /// L3 contract: the only surface a game must implement to become directable.
    /// Everything here is expressed in DSL vocabulary (role ids, location ids,
    /// clip names, shot specs) — the kernel never sees engine types.
    ///
    /// Implementations must be callable on the engine's main thread; the Unity
    /// bridge guarantees this by marshalling. Implementations should be
    /// fire-and-forget; long-running motion (move/dolly) is owned by the
    /// adapter and driven by its own update loop.
    /// </summary>
    public interface IGameDirectorAdapter
    {
        /// <summary>Begin (or replace) the current camera shot. DurationSeconds=0 means hold until the next shot cue.</summary>
        void ApplyCameraShot(ShotSpec shot);

        /// <summary>Cross-fade the role's current animation to the given clip.</summary>
        void PlayAnimation(string role, string clip, float fadeSeconds);

        /// <summary>Move the role toward a manifest location. Adapter decides locomotion (navmesh, tween, physics).</summary>
        void MoveRole(string role, string toLocationId, float speed, string headingTo);

        /// <summary>Rotate the role toward another role id or a location id.</summary>
        void FaceRole(string role, string roleOrLocationId);

        /// <summary>Instantiate the role at a manifest location, optionally facing a role/location id.</summary>
        void SpawnRole(string role, string locationId, string headingTo);

        /// <summary>Remove the role from the scene.</summary>
        void DespawnRole(string role);

        void PlayAudio(string audioId, float volume);
        void StopAudio(string audioId);

        /// <summary>Set world time scale (restores are explicit cues compiled into the timeline).</summary>
        void SetTimeScale(float scale);

        /// <summary>Pure annotation beat — adapters may log it; the sink always records it.</summary>
        void Marker(string label);
    }
}
