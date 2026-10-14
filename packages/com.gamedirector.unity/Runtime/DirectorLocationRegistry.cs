using System.Collections.Generic;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Scene location registry: ONE component per scene, locations are its child
    /// GameObjects (name = location id, pose = child transform).
    ///
    /// A single registry keeps location authoring lightweight: each child
    /// Transform supplies the id and pose without requiring another script
    /// component. Location ids must be unique within the director scene.
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
                if (found) throw new StageBindingException("duplicate location id in scene: " + id);
                found = true;
                pos = child.position;
                headingDeg = child.eulerAngles.y;
            }
            return found;
        }
    }

}
