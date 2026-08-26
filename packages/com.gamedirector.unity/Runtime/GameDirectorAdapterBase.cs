using System.Collections;
using System.Collections.Generic;
using GameDirector.Core.Adapters;
using GameDirector.Core.Dsl;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// L3 adapter base for Unity games. Implements the generic 80%: role/location
    /// resolution via scene markers, Animator cross-fade, tween-style locomotion,
    /// facing, camera-shot delegation to CinematicCameraRig, and time scale.
    /// Game adapters override spawning, audio, and manifest construction —
    /// the three places where games genuinely differ.
    ///
    /// Authority rule: adapters only ever touch presentation state they own.
    /// In a networked game, v0 expects an offline/sandbox runner; live-network
    /// direction (Host fan-out) is a v1 design point documented per adapter.
    /// </summary>
    public abstract class GameDirectorAdapterBase : MonoBehaviour, IGameDirectorAdapter
    {
        private CapabilityManifest _manifest;
        private CinematicCameraRig _rig;
        private readonly Dictionary<string, Transform> _dynamicRoles = new Dictionary<string, Transform>();
        private readonly Dictionary<string, Coroutine> _movers = new Dictionary<string, Coroutine>();

        /// <summary>The game self-description served to external directors.</summary>
        public CapabilityManifest Manifest => _manifest ??= BuildManifest();

        protected abstract CapabilityManifest BuildManifest();

        protected virtual void Awake()
        {
            _rig = GetComponent<CinematicCameraRig>() ?? gameObject.AddComponent<CinematicCameraRig>();
        }

        // ---- resolution ----

        protected Transform ResolveRole(string roleId)
        {
            if (string.IsNullOrEmpty(roleId)) return null;
            if (_dynamicRoles.TryGetValue(roleId, out var t) && t != null) return t;
            return DirectorRoleRegistry.Find(roleId);
        }

        protected bool TryResolvePose(string locationId, out Vector3 pos, out float headingDeg)
        {
            pos = default; headingDeg = 0f;
            if (string.IsNullOrEmpty(locationId)) return false;
            if (DirectorLocationRegistry.FindPose(locationId, out pos, out headingDeg))
                return true;
            // Fall back to manifest coordinates (authoring-time truth).
            var loc = Manifest?.FindLocation(locationId);
            if (loc?.Position != null && loc.Position.Length == 3)
            {
                pos = new Vector3(loc.Position[0], loc.Position[1], loc.Position[2]);
                headingDeg = loc.HeadingDeg;
                return true;
            }
            return false;
        }

        // ---- IGameDirectorAdapter ----

        public virtual void ApplyCameraShot(ShotSpec shot)
        {
            _rig.BeginShot(shot, ResolveRole, TryResolvePose);
        }

        public virtual void PlayAnimation(string role, string clip, float fadeSeconds)
        {
            var t = ResolveRole(role);
            if (t == null) { Debug.LogWarning($"[GameDirector] anim: role '{role}' not resolved"); return; }
            var animator = t.GetComponentInChildren<Animator>();
            if (animator == null) { Debug.LogWarning($"[GameDirector] anim: no Animator under '{role}'"); return; }
            animator.CrossFade(clip, Mathf.Max(0f, fadeSeconds));
        }

        public virtual void MoveRole(string role, string toLocationId, float speed, string headingTo)
        {
            var t = ResolveRole(role);
            if (t == null || !TryResolvePose(toLocationId, out var target, out _)) return;
            StopMover(role);
            _movers[role] = StartCoroutine(MoveRoutine(t, target, Mathf.Max(0.01f, speed), headingTo));
        }

        public virtual void FaceRole(string role, string roleOrLocationId)
        {
            var t = ResolveRole(role);
            if (t == null) return;
            var other = ResolveRole(roleOrLocationId);
            Vector3 target = other != null
                ? other.position
                : TryResolvePose(roleOrLocationId, out var p, out _) ? p : t.position + t.forward;
            FaceTowards(t, target);
        }

        public virtual void SpawnRole(string role, string locationId, string headingTo)
        {
            if (!TryResolvePose(locationId, out var pos, out var heading))
            {
                Debug.LogWarning($"[GameDirector] spawn: location '{locationId}' not resolved");
                return;
            }
            var instance = SpawnRoleInstance(role, pos, Quaternion.Euler(0f, heading, 0f));
            if (instance == null) return;
            _dynamicRoles[role] = instance;
            if (!string.IsNullOrEmpty(headingTo)) FaceRole(role, headingTo);
        }

        public virtual void DespawnRole(string role)
        {
            StopMover(role);
            if (_dynamicRoles.TryGetValue(role, out var t) && t != null)
            {
                _dynamicRoles.Remove(role);
                Destroy(t.gameObject);
            }
        }

        public virtual void SetTimeScale(float scale) => Time.timeScale = Mathf.Clamp(scale, 0.01f, 10f);

        public virtual void Marker(string label) => Debug.Log($"[GameDirector] marker: {label}");

        public abstract void PlayAudio(string audioId, float volume);
        public abstract void StopAudio(string audioId);

        /// <summary>Game-specific spawn hook. Return the instance root transform, or null to log-and-skip.</summary>
        protected abstract Transform SpawnRoleInstance(string role, Vector3 pos, Quaternion rot);

        // ---- helpers ----

        protected static void FaceTowards(Transform t, Vector3 target)
        {
            var dir = target - t.position; dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f) t.rotation = Quaternion.LookRotation(dir);
        }

        private void StopMover(string role)
        {
            if (_movers.TryGetValue(role, out var c) && c != null) StopCoroutine(c);
            _movers.Remove(role);
        }

        private IEnumerator MoveRoutine(Transform t, Vector3 target, float speed, string headingTo)
        {
            while (t != null)
            {
                var flat = target - t.position; flat.y = 0f;
                if (flat.magnitude < 0.1f) break;
                var step = Mathf.Min(speed * Time.unscaledDeltaTime, flat.magnitude);
                t.position += flat.normalized * step;
                FaceTowards(t, target);
                yield return null;
            }
            if (t != null && !string.IsNullOrEmpty(headingTo))
            {
                var look = ResolveRole(headingTo);
                if (look != null) FaceTowards(t, look.position);
                else if (TryResolvePose(headingTo, out var p, out _)) FaceTowards(t, p);
            }
        }
    }
}
