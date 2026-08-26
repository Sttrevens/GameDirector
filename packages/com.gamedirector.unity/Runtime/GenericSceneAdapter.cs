using System.Collections.Generic;
using GameDirector.Core.Dsl;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Default adapter for quick bring-up in any Unity scene: resolves roles via
    /// DirectorRoleBinding markers, spawns from an inspector-assigned prefab map,
    /// plays audio from an inspector-assigned clip map. Games outgrow it by
    /// subclassing GameDirectorAdapterBase (see adapters/cdrebirth).
    /// </summary>
    public sealed class GenericSceneAdapter : GameDirectorAdapterBase
    {
        [System.Serializable]
        public sealed class PrefabEntry { public string roleId; public GameObject prefab; }
        [System.Serializable]
        public sealed class AudioEntry { public string audioId; public AudioClip clip; }

        [SerializeField] private TextAsset manifestJson;
        [SerializeField] private List<PrefabEntry> prefabs = new List<PrefabEntry>();
        [SerializeField] private List<AudioEntry> audioClips = new List<AudioEntry>();

        private AudioSource _source;

        protected override CapabilityManifest BuildManifest()
        {
            if (manifestJson != null)
                return BridgeJson.Deserialize<CapabilityManifest>(manifestJson.text);
            Debug.LogWarning("[GameDirector] GenericSceneAdapter has no manifestJson assigned; serving an empty manifest");
            return new CapabilityManifest { Game = "unconfigured" };
        }

        protected override Transform SpawnRoleInstance(string role, Vector3 pos, Quaternion rot)
        {
            var entry = prefabs.Find(p => p.roleId == role && p.prefab != null);
            if (entry == null)
            {
                Debug.LogWarning($"[GameDirector] no prefab registered for role '{role}'; spawn skipped");
                return null;
            }
            return Instantiate(entry.prefab, pos, rot).transform;
        }

        public override void PlayAudio(string audioId, float volume)
        {
            var entry = audioClips.Find(a => a.audioId == audioId && a.clip != null);
            if (entry == null) { Debug.LogWarning($"[GameDirector] no clip registered for audio '{audioId}'"); return; }
            if (_source == null) _source = gameObject.AddComponent<AudioSource>();
            _source.clip = entry.clip;
            _source.volume = Mathf.Clamp01(volume);
            _source.Play();
        }

        public override void StopAudio(string audioId)
        {
            if (_source != null && _source.isPlaying) _source.Stop();
        }
    }
}
