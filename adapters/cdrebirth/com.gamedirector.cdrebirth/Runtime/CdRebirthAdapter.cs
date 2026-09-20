using GameDirector.Core.Dsl;
using GameDirector.Unity;
using UnityEngine;

namespace GameDirector.CDREBIRTH
{
    /// <summary>
    /// CDREBIRTH adapter (L3) — v0 offline director-sandbox only.
    ///
    /// Authority lens (Fusion Host/Client), per CDREBIRTH Docs/Network rules:
    ///   v0: everything here runs in a LOCAL, non-networked sandbox scene
    ///       (no NetworkRunner). Camera, spawned monsters, animations, time
    ///       scale are all local-only presentation state — no authority question.
    ///   v1 (not implemented): Host-directed live filming. All spawn/anim/state
    ///       cues would become StateAuthority-owned mutations fanned out via RPC;
    ///       camera/capture stay peer-local. Do NOT add networking ad hoc.
    ///
    /// Coexistence with the in-game AI Director (DM): the DM is gameplay-truth
    /// (tasks/scoring). Director mode must run with DM/LiveShow systems disabled
    /// or absent (dedicated sandbox scene), otherwise two directors fight.
    ///
    /// Asset references and scene registries bind actual game actors. Owned
    /// actors are isolated before activation; capture uses the frame clock.
    /// </summary>
    public sealed class CdRebirthAdapter : GameDirectorAdapterBase
    {
        [SerializeField] private TextAsset manifestJson;
        [SerializeField] private GameObject bigGuaiPrefab;
        [SerializeField] private GameObject speakerMonsterPrefab;

        protected override CapabilityManifest BuildManifest()
        {
            if (manifestJson != null)
                return BridgeJson.Deserialize<CapabilityManifest>(manifestJson.text);
            Debug.LogWarning("[GameDirector.CDREBIRTH] manifestJson not assigned; serving empty manifest");
            return new CapabilityManifest { Game = "CDREBIRTH" };
        }

        protected override Transform SpawnRoleInstance(string role, Vector3 pos, Quaternion rot)
        {
            var prefab = role == "bigguai" ? bigGuaiPrefab
                       : role == "speaker" ? speakerMonsterPrefab
                       : null;
            if (prefab == null)
            {
                Debug.LogWarning($"[GameDirector.CDREBIRTH] no prefab bound for role '{role}'; spawn skipped");
                return null;
            }
            var instance = PresentationMode.CreateActor(prefab, pos, rot);
            return instance.transform;
        }

        protected override GameObject SpawnPrefab(string role) => role == "bigguai" ? bigGuaiPrefab : role == "speaker" ? speakerMonsterPrefab : null;

        public override void Preflight(TimelineAsset asset)
        {
            if (!gameObject.scene.path.StartsWith("Assets/Scenes/GameDirector/", System.StringComparison.Ordinal))
                throw new System.InvalidOperationException("CDREBIRTH directing requires its dedicated offline sandbox scene");
            // No Fusion dependency in the adapter package; inspect loaded runners by type.
            foreach (var b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
                if (b != null && b.GetType().FullName == "Fusion.NetworkRunner")
                    throw new System.InvalidOperationException("NetworkRunner present: offline directing refused");
            base.Preflight(asset);
        }
    }
}
