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
    /// TODO(M1 wiring, requires CDREBIRTH project + open Editor):
    ///   1. Reference the game assemblies and resolve "hero" to the local
    ///      PlayerMovement transform instead of a marker (fallback stays markers).
    ///   2. Load BigGuai/SpeakerMonster prefabs from the project's PGC sources
    ///      for SpawnRoleInstance (paths below are placeholders).
    ///   3. Place DirectorLocationAnchor objects in a grimforest sandbox copy
    ///      matching gf_gate / gf_stage / gf_cam_* ids in the manifest.
    ///   4. M2: route capture through AVPro Movie Capture for video takes.
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
            // v0 offline spawn: plain Instantiate. No Fusion runner exists in the
            // sandbox scene, so nothing here is networked or needs StateAuthority.
            var prefab = role == "bigguai" ? bigGuaiPrefab
                       : role == "speaker" ? speakerMonsterPrefab
                       : null;
            if (prefab == null)
            {
                Debug.LogWarning($"[GameDirector.CDREBIRTH] no prefab bound for role '{role}'; spawn skipped");
                return null;
            }
            var instance = Instantiate(prefab, pos, rot);
            // TODO(M1): disable monster AI/aggro components here so the timeline,
            // not the game's AI, owns the performance (e.g. set monsters to a
            // director-controlled presentation mode).
            return instance.transform;
        }

        public override void PlayAudio(string audioId, float volume)
        {
            // TODO(M1): bind to the project's audio service / mixer groups.
            Debug.Log($"[GameDirector.CDREBIRTH] audio.play {audioId} vol={volume} (audio service wiring pending)");
        }

        public override void StopAudio(string audioId)
        {
            Debug.Log($"[GameDirector.CDREBIRTH] audio.stop {audioId} (audio service wiring pending)");
        }
    }
}
