using System;
using System.Collections.Generic;
using GameDirector.Core.Dsl;

namespace GameDirector.Core.Adapters
{
    /// <summary>
    /// Test/dev adapter that records every call. Also serves as the executable
    /// specification of adapter semantics (ordering, arguments).
    /// </summary>
    public sealed class RecordingAdapter : IGameDirectorAdapter
    {
        public readonly List<string> Calls = new List<string>();

        public void ApplyCameraShot(ShotSpec shot) =>
            Calls.Add("shot:" + (shot != null ? shot.Type + "|" + shot.Frame + "|" + shot.Subject : "null"));
        public void PlayAnimation(string role, string clip, float fadeSeconds) =>
            Calls.Add("anim:" + role + "|" + clip + "|" + fadeSeconds.ToString("0.###"));
        public void MoveRole(string role, string toLocationId, float speed, string headingTo) =>
            Calls.Add("move:" + role + "|" + toLocationId + "|" + speed.ToString("0.###") + "|" + (headingTo ?? ""));
        public void FaceRole(string role, string roleOrLocationId) =>
            Calls.Add("face:" + role + "|" + roleOrLocationId);
        public void SpawnRole(string role, string locationId, string headingTo) =>
            Calls.Add("spawn:" + role + "|" + locationId + "|" + (headingTo ?? ""));
        public void DespawnRole(string role) => Calls.Add("despawn:" + role);
        public void PlayAudio(string audioId, float volume) =>
            Calls.Add("audio:" + audioId + "|" + volume.ToString("0.###"));
        public void StopAudio(string audioId) => Calls.Add("audiostop:" + audioId);
        public void SetTimeScale(float scale) => Calls.Add("timescale:" + scale.ToString("0.###"));
        public void Marker(string label) => Calls.Add("marker:" + label);
    }
}
