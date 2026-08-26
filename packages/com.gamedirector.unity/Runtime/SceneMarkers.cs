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
        public static bool FindPose(string id, out Vector3 pos, out float headingDeg)
        {
            pos = default; headingDeg = 0f;
            if (string.IsNullOrEmpty(id)) return false;
            EnsurePopulated();
            for (int i = 0; i < Active.Count; i++)
            {
                var reg = Active[i];
                if (reg == null) continue;
                var child = reg.transform.Find(id);
                if (child == null) continue;
                pos = child.position;
                headingDeg = child.eulerAngles.y;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Scene role registry: ONE component per scene, entries map role ids to
    /// existing scene Transforms (e.g. the hero). References point at native
    /// Transform components — immune to the binary-scene scripted-type fileID
    /// collision described above. Dynamically spawned roles bypass this registry
    /// entirely (the adapter tracks them directly).
    /// </summary>
    public sealed class DirectorRoleRegistry : MonoBehaviour
    {
        [System.Serializable]
        public sealed class Entry
        {
            public string roleId;
            public Transform target;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();
        public IReadOnlyList<Entry> Entries => entries;

        private static readonly List<DirectorRoleRegistry> Active = new List<DirectorRoleRegistry>();

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        private static void EnsurePopulated()
        {
            Active.RemoveAll(r => r == null);
            if (Active.Count != 0) return;
            foreach (var reg in Object.FindObjectsOfType<DirectorRoleRegistry>(true))
                if (!Active.Contains(reg)) Active.Add(reg);
        }

        public static Transform Find(string roleId)
        {
            if (string.IsNullOrEmpty(roleId)) return null;
            EnsurePopulated();
            for (int i = 0; i < Active.Count; i++)
            {
                var reg = Active[i];
                if (reg == null) continue;
                for (int j = 0; j < reg.entries.Count; j++)
                {
                    var e = reg.entries[j];
                    if (e != null && e.roleId == roleId && e.target != null) return e.target;
                }
            }
            return null;
        }
    }
}
