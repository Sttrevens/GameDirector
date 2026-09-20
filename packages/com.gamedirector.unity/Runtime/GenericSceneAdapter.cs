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

        protected override GameObject SpawnPrefab(string role) => prefabs.Find(p => p.roleId == role)?.prefab;
        protected override void Awake() {
            base.Awake();
            foreach(var entry in audioClips) if(entry.clip != null) audioBindings.Add(new AudioBinding {audioId=entry.audioId,clip=entry.clip});
        }

        protected override CapabilityManifest BuildManifest()
        {
            if (manifestJson != null)
                return BridgeJson.Deserialize<CapabilityManifest>(manifestJson.text);
            // The advertised vocabulary is the shared registry's vocabulary: a
            // new built-in shot/frame type appears here by being registered, not
            // by editing this adapter.
            var manifest = new CapabilityManifest { Game = Application.productName,
                ShotTypes = ShotVocabulary.ShotIds,
                FrameTypes = ShotVocabulary.FrameIds };
            if (OfflinePresentationScene.Owns(gameObject.scene)) manifest.Capabilities[CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox;
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                foreach (var registry in root.GetComponentsInChildren<DirectorRoleRegistry>(true))
                {
                    if (!registry.isActiveAndEnabled) continue;
                    foreach (var entry in registry.Entries)
                    {
                        if (entry == null || entry.target == null || entry.target.gameObject.scene != gameObject.scene || string.IsNullOrEmpty(entry.roleId)) continue;
                        var actor = new ActorDescriptor { Id = entry.roleId + "-actor", DisplayName = entry.target.name };
#if UNITY_EDITOR
                        var animator = entry.target.GetComponentInChildren<Animator>(true);
                        var controller = animator != null ? animator.runtimeAnimatorController : null;
                        if (controller is AnimatorOverrideController replacement) controller = replacement.runtimeAnimatorController;
                        if (controller is UnityEditor.Animations.AnimatorController authored && authored.layers.Length > 0)
                            AddStates(authored.layers[0].stateMachine, authored.layers[0].name, actor.Clips);
#endif
                        manifest.Actors.Add(actor);
                        manifest.Roles.Add(new RoleDescriptor { Id = entry.roleId, DisplayName = entry.target.name, Kind = "actor", DefaultActor = actor.Id, PresentAtStart = true });
                    }
                }
                foreach (var registry in root.GetComponentsInChildren<DirectorLocationRegistry>(true))
                {
                    if (!registry.isActiveAndEnabled) continue;
                    foreach (Transform anchor in registry.transform)
                        manifest.Locations.Add(new LocationDescriptor { Id = anchor.name, Space = registry.SpaceId,
                            Position = new[] { anchor.position.x, anchor.position.y, anchor.position.z }, HeadingDeg = anchor.eulerAngles.y });
                }
            }
            foreach (var entry in audioClips) if (entry.clip != null) manifest.Audio.Add(new AudioDescriptor { Id = entry.audioId, Kind = "sfx" });
            return manifest;
        }

#if UNITY_EDITOR
        private static void AddStates(UnityEditor.Animations.AnimatorStateMachine machine, string path, List<string> states)
        {
            foreach (var state in machine.states) if (state.state.motion != null) states.Add(path + "." + state.state.name);
            foreach (var child in machine.stateMachines) AddStates(child.stateMachine, path + "." + child.stateMachine.name, states);
        }
#endif

        protected override Transform SpawnRoleInstance(string role, Vector3 pos, Quaternion rot)
        {
            var entry = prefabs.Find(p => p.roleId == role && p.prefab != null);
            if (entry == null)
            {
                Debug.LogWarning($"[GameDirector] no prefab registered for role '{role}'; spawn skipped");
                return null;
            }
            return PresentationMode.CreateActor(entry.prefab, pos, rot).transform;
        }

    }
}
