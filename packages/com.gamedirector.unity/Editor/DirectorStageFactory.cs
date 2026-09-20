using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.Unity.Editor
{
    public static class DirectorStageFactory
    {
        // Explicit authoring action: creates a NEW saved offline stage. Existing
        // scenes/prefabs are never saved, replaced or reconfigured.
        public static string Create(GameObject prefab)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || prefab == null || !EditorUtility.IsPersistent(prefab))
                throw new ArgumentException("Select a prefab/model asset in Edit Mode.");
            // Unity refuses additive scene creation while an untitled scene is
            // loaded. Check before writing folders/assets; never save the user's
            // untitled work or switch scenes on their behalf to bypass that rule.
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.IsNullOrEmpty(scene.path) && !EditorSceneManager.IsPreviewScene(scene))
                    throw new InvalidOperationException("Unity requires untitled scenes to be saved or closed before creating an additive stage. Save or close them yourself, then try again; no stage files were written.");
            }
            var prior = SceneManager.GetActiveScene();
            EnsureFolder("Assets", "Scenes"); EnsureFolder("Assets/Scenes", "GameDirector");
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/Scenes/GameDirector/DirectorStage.unity");
            var stage = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                if (stage.IsValid() && stage.isLoaded && SceneManager.GetActiveScene() != stage) SceneManager.SetActiveScene(stage);
                if (!stage.IsValid() || !stage.isLoaded || SceneManager.GetActiveScene() != stage)
                    throw new InvalidOperationException("Unity could not activate the new offline stage (valid=" + stage.IsValid() + ", loaded=" + stage.isLoaded + ").");
                var actor = PresentationMode.CreateActor(prefab, Vector3.zero, Quaternion.identity);
                SceneManager.MoveGameObjectToScene(actor, stage); actor.SetActive(true);
                var root = new GameObject("GameDirector"); root.AddComponent<OfflinePresentationScene>();
                root.AddComponent<GenericSceneAdapter>(); root.AddComponent<CinematicCameraRig>(); root.AddComponent<DirectorRuntime>(); root.AddComponent<DirectorBridgeServer>();
                var registry = root.AddComponent<DirectorRoleRegistry>();
                var serialized = new SerializedObject(registry); var entries = serialized.FindProperty("entries"); entries.arraySize = 1;
                entries.GetArrayElementAtIndex(0).FindPropertyRelative("roleId").stringValue = DirectorStageVocabulary.LeadRoleId;
                entries.GetArrayElementAtIndex(0).FindPropertyRelative("target").objectReferenceValue = actor.transform;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var bounds = new Bounds(actor.transform.position, Vector3.one);
                foreach (var renderer in actor.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(renderer.bounds);
                var center = bounds.center; var size = Mathf.Max(1, bounds.size.magnitude);
                var anchors = new GameObject("Camera anchors"); anchors.AddComponent<DirectorLocationRegistry>();
                Anchor(anchors.transform, DirectorStageVocabulary.WideAnchor, center + new Vector3(size * .3f, size * .18f, size * 1.1f));
                Anchor(anchors.transform, DirectorStageVocabulary.CloseAnchor, center + new Vector3(size * .12f, size * .08f, size * .55f));
                Anchor(anchors.transform, DirectorStageVocabulary.SideAnchor, center + new Vector3(size, size * .15f, size * .4f));
                var key = new GameObject("Key light").AddComponent<Light>(); key.type = LightType.Directional; key.intensity = 1.1f; key.transform.rotation = Quaternion.Euler(35, -35, 0);
                var fill = new GameObject("Fill light").AddComponent<Light>(); fill.type = LightType.Directional; fill.intensity = .35f; fill.color = new Color(.7f, .8f, 1); fill.transform.rotation = Quaternion.Euler(20, 145, 0);
                if (!EditorSceneManager.SaveScene(stage, path)) throw new InvalidOperationException("Could not save the new stage.");
                return path;
            }
            finally { if (prior.IsValid() && prior.isLoaded) SceneManager.SetActiveScene(prior); if (stage.IsValid() && stage.isLoaded) EditorSceneManager.CloseScene(stage, true); }
        }
        private static void Anchor(Transform parent, string name, Vector3 position) { var anchor = new GameObject(name).transform; anchor.SetParent(parent); anchor.position = position; }
        private static void EnsureFolder(string parent, string name) { if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name); }
    }
}
