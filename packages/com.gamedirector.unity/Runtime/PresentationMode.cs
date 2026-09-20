using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>A reversible lease over an offline actor's presentation.</summary>
    public static class PresentationMode
    {
        /// <summary>Clone under an inactive root so gameplay Awake/OnEnable cannot
        /// run before ownership is established. Owned film actors keep native
        /// rendering/animation components and explicitly clocked presentation participants.</summary>
        public static GameObject CreateActor(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var staging = new GameObject("DirectorSpawnStaging");
            staging.SetActive(false);
            GameObject actor = null;
            try {
                actor = UnityEngine.Object.Instantiate(prefab, position, rotation, staging.transform);
                actor.SetActive(false);
                // RequireComponent dependencies can require dependent-first removal.
                while (true) {
                    var scripts = actor.GetComponentsInChildren<MonoBehaviour>(true);
                    int remaining = Array.FindAll(scripts, s => !(s is IDirectorPresentationParticipant)).Length;
                    if (remaining == 0) break;
                    for (int i = scripts.Length - 1; i >= 0; i--)
                        if (scripts[i] != null && !(scripts[i] is IDirectorPresentationParticipant) && CanRemove(scripts[i], scripts)) UnityEngine.Object.DestroyImmediate(scripts[i]);
                    if (Array.FindAll(actor.GetComponentsInChildren<MonoBehaviour>(true), s => !(s is IDirectorPresentationParticipant)).Length >= remaining)
                        throw new InvalidOperationException("actor has inseparable gameplay components: " + prefab.name);
                }
                if (Array.Exists(actor.GetComponentsInChildren<MonoBehaviour>(true), s => !(s is IDirectorPresentationParticipant)))
                    throw new InvalidOperationException("actor gameplay isolation failed: " + prefab.name);
                // Native OnEnable also has side effects: NavMesh placement,
                // cameras, gravity and AudioSource.playOnAwake must be suppressed
                // before activation. Owned actors never restore this lease.
                foreach (var b in actor.GetComponentsInChildren<Behaviour>(true))
                    if (b is MonoBehaviour || b is Camera || b is UnityEngine.AI.NavMeshAgent) b.enabled = false;
                // Owned presentation actors have no autonomous animation clock.
                // Otherwise merely waiting between manifest and take/start changes
                // their pose and correctly invalidates the source fingerprint.
                // OwnActor/PlayAnimation/Advance explicitly sample at director time.
                // enabled is serialized into an authored stage; speed alone is not.
                foreach (var animator in actor.GetComponentsInChildren<Animator>(true)) { animator.speed = 0; animator.enabled = false; }
                foreach (var rb in actor.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic=true; rb.useGravity=false; }
                foreach (var audio in actor.GetComponentsInChildren<AudioSource>(true)) {
                    audio.playOnAwake = false; audio.Stop(); audio.enabled = false;
                }
                actor.transform.SetParent(null, true);
                return actor;
            } catch {
                if (actor != null) UnityEngine.Object.DestroyImmediate(actor);
                throw;
            } finally { UnityEngine.Object.DestroyImmediate(staging); }
        }

        private static bool CanRemove(MonoBehaviour candidate, MonoBehaviour[] scripts)
        {
            var type = candidate.GetType();
            foreach (var other in scripts) {
                if (other == null || other == candidate || other.gameObject != candidate.gameObject) continue;
                foreach (RequireComponent requirement in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    if ((requirement.m_Type0 != null && requirement.m_Type0.IsAssignableFrom(type)) ||
                        (requirement.m_Type1 != null && requirement.m_Type1.IsAssignableFrom(type)) ||
                        (requirement.m_Type2 != null && requirement.m_Type2.IsAssignableFrom(type))) return false;
            }
            return true;
        }

        public sealed class Lease : IDisposable
        {
            private readonly List<Action> _restore = new List<Action>();
            private readonly List<IDirectorPresentationParticipant> _participants = new List<IDirectorPresentationParticipant>();
            private bool _prepared;
            public Lease(GameObject root)
            {
                // Snapshot script-owned visuals before disabling any callbacks.
                foreach (var script in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (script is IDirectorPresentationParticipant p) {
                        var restore = p.CaptureState();
                        if (restore == null) throw new InvalidOperationException("presentation participant must provide a restore action: " + script.GetType().Name);
                        _participants.Add(p); _restore.Add(restore);
                    }
                try {
                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                {
                    // Preserve render lights and audio; only scripts, navigation,
                    // cameras and physics controllers compete with the director.
                    if (!(b is MonoBehaviour) && !(b is UnityEngine.AI.NavMeshAgent) && !(b is Camera)) continue;
                    bool enabled = b.enabled;
                    _restore.Add(() => { if (b != null) b.enabled = enabled; });
                    b.enabled = false;
                }
                foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true)) {
                    bool k = rb.isKinematic; bool g = rb.useGravity;
                    var velocity = rb.velocity; var angular = rb.angularVelocity;
                    _restore.Add(() => { if (rb != null) { rb.isKinematic = k; rb.useGravity = g; if (!k) {rb.velocity=velocity;rb.angularVelocity=angular;} } });
                    rb.isKinematic = true; rb.useGravity = false;
                }
                } catch { Dispose(); throw; }
            }
            public void Prepare() {
                if (_prepared) return;
                _prepared = true;
                foreach (var p in _participants) p.PreparePresentation();
            }
            public void Evaluate(DirectorPresentationPhase phase, double dt, double time) {
                if (!_prepared) return;
                foreach (var p in _participants) if (!(p is MonoBehaviour b) || (b != null && b.gameObject.activeInHierarchy))
                    if (p.Phase == phase) p.EvaluatePresentation(dt, time);
            }
            public void Dispose() {
                Exception failure = null;
                // Restore visual state before callbacks are re-enabled.
                for (int i = _participants.Count - 1; i >= 0; i--) try { _restore[i](); } catch(Exception ex) { failure = ex; }
                for (int i = _restore.Count - 1; i >= _participants.Count; i--) try { _restore[i](); } catch(Exception ex) { failure = ex; }
                _restore.Clear(); _participants.Clear(); _prepared = false;
                if (failure != null) throw new InvalidOperationException("presentation restore failed", failure);
            }
        }
        public static Lease Acquire(GameObject root) => new Lease(root);
        // Kept for existing offline scene setup. Session code uses Acquire/Dispose.
        public static void Apply(GameObject root) { if(root!=null) _ = new Lease(root); }
    }
}
