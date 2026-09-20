using System.Collections.Generic;
using UnityEngine;

namespace GameDirector.Unity
{
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

        public static Transform Find(string roleId, UnityEngine.SceneManagement.Scene? scene = null)
        {
            if (string.IsNullOrEmpty(roleId)) return null;
            EnsurePopulated();
            Transform found = null;
            for (int i = 0; i < Active.Count; i++)
            {
                var reg = Active[i];
                if (reg == null || !reg.isActiveAndEnabled || (scene.HasValue && reg.gameObject.scene != scene.Value)) continue;
                for (int j = 0; j < reg.entries.Count; j++)
                {
                    var e = reg.entries[j];
                    if (e != null && e.roleId == roleId && e.target != null) {
                        if (found != null) throw new System.InvalidOperationException("duplicate role id in scene: " + roleId);
                        found = e.target;
                    }
                }
            }
            return found;
        }
    }
}
