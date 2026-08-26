using System.Collections.Generic;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Scene marker: binds a GameObject to a DSL role id (e.g. "hero", "bigguai").
    /// The adapter resolves roles through this registry first, so nothing in the
    /// kernel depends on scene object names.
    /// </summary>
    public sealed class DirectorRoleBinding : MonoBehaviour
    {
        [SerializeField] private string roleId;
        public string RoleId => roleId;

        private static readonly List<DirectorRoleBinding> Active = new List<DirectorRoleBinding>();

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        public static DirectorRoleBinding Find(string id)
        {
            for (int i = 0; i < Active.Count; i++)
                if (Active[i] != null && Active[i].roleId == id) return Active[i];
            return null;
        }
    }

    /// <summary>
    /// Scene marker: names a world-space pose as a DSL location id (e.g. "gf_gate").
    /// Manifest position values are authoring-time documentation; runtime truth
    /// is this anchor's transform.
    /// </summary>
    public sealed class DirectorLocationAnchor : MonoBehaviour
    {
        [SerializeField] private string locationId;
        [SerializeField] private string spaceId = "default";
        public string LocationId => locationId;
        public string SpaceId => spaceId;

        private static readonly List<DirectorLocationAnchor> Active = new List<DirectorLocationAnchor>();

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        public static DirectorLocationAnchor Find(string id)
        {
            for (int i = 0; i < Active.Count; i++)
                if (Active[i] != null && Active[i].locationId == id) return Active[i];
            return null;
        }
    }
}
