using UnityEngine;
using UnityEngine.AI;

namespace GameDirector.Unity
{
    /// <summary>
    /// Puts a spawned/placed actor into director-controlled presentation mode:
    /// every Behaviour except Animator is disabled, and Rigidbodies become
    /// kinematic. The timeline — not the game's AI, input, or networking —
    /// owns the performance. This is the v0 offline-sandbox contract; live
    /// networked direction (v1) needs a different, authority-aware path.
    /// </summary>
    public static class PresentationMode
    {
        public static void Apply(GameObject root)
        {
            if (root == null) return;
            foreach (var b in root.GetComponentsInChildren<Behaviour>(includeInactive: true))
            {
                if (b is Animator) continue;
                b.enabled = false;
            }
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(includeInactive: true))
                rb.isKinematic = true;
            foreach (var agent in root.GetComponentsInChildren<NavMeshAgent>(includeInactive: true))
                if (agent.enabled) agent.enabled = false;
        }
    }
}
