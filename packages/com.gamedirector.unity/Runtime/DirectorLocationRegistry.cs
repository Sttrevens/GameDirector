using System.Collections.Generic;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Scene location registry: ONE component per scene, locations are its child
    /// GameObjects (name = location id, pose = child transform).
    ///
    /// Why a registry instead of one marker component per location: CDREBIRTH
    /// serializes scenes in BINARY mode, and multiple MonoBehaviours of the same
    /// scripted type in one binary scene can collide on local fileIDs and get
    /// silently dropped on load (observed 2026-08-26: 3 of 5 anchors lost per
    /// save/reload round-trip). Native components (Transform) and object
    /// references are unaffected, so children-as-locations plus a single
    /// scripted registry is collision-proof by construction.
    /// </summary>
    public sealed class DirectorLocationRegistry : MonoBehaviour
    {
        [SerializeField] private string spaceId = "default";
        public string SpaceId => spaceId;

        private static readonly List<DirectorLocationRegistry> Active = new List<DirectorLocationRegistry>();

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        // Edit mode never fires OnEnable/OnDisable (no [ExecuteAlways]), and
        // scene reloads leave DESTROYED objects in this static list — fake-null
        // entries must be pruned, then an empty cache re-sweeps. Safe in edit
        // mode, play mode, and across domain/scene reloads.
        private static void EnsurePopulated()
        {
            Active.RemoveAll(r => r == null);
            if (Active.Count != 0) return;
            foreach (var reg in Object.FindObjectsOfType<DirectorLocationRegistry>(true))
                if (!Active.Contains(reg)) Active.Add(reg);
        }

        /// <summary>Find a location pose by id across all active registries (child name = id).</summary>
        public static bool FindPose(string id, out Vector3 pos, out float headingDeg, UnityEngine.SceneManagement.Scene? scene = null)
        {
            pos = default; headingDeg = 0f;
            if (string.IsNullOrEmpty(id)) return false;
            EnsurePopulated();
            bool found = false;
            for (int i = 0; i < Active.Count; i++)
            {
                var reg = Active[i];
                if (reg == null || !reg.isActiveAndEnabled || (scene.HasValue && reg.gameObject.scene != scene.Value)) continue;
                var child = reg.transform.Find(id);
                if (child == null) continue;
                if (found) throw new System.InvalidOperationException("duplicate location id in scene: " + id);
                found = true;
                pos = child.position;
                headingDeg = child.eulerAngles.y;
            }
            return found;
        }
    }

}
