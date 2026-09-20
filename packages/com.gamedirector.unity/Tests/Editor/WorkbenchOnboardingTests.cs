using System.Linq;
using GameDirector.Unity.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.Unity.Tests
{
    public class WorkbenchOnboardingTests
    {
        [Test] public void DisabledRegistriesDoNotAdvertiseUnresolvableBindings()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("director"); SceneManager.MoveGameObjectToScene(root, scene); root.AddComponent<OfflinePresentationScene>();
                var adapter = root.AddComponent<GenericSceneAdapter>();
                var registry = root.AddComponent<DirectorRoleRegistry>(); registry.enabled = false;
                var serialized = new SerializedObject(registry); var entries = serialized.FindProperty("entries"); entries.arraySize = 1;
                entries.GetArrayElementAtIndex(0).FindPropertyRelative("roleId").stringValue = "disabled";
                entries.GetArrayElementAtIndex(0).FindPropertyRelative("target").objectReferenceValue = root.transform;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var anchors = new GameObject("inactive anchors"); SceneManager.MoveGameObjectToScene(anchors, scene);
                anchors.AddComponent<DirectorLocationRegistry>(); var anchor = new GameObject("hidden"); anchor.transform.SetParent(anchors.transform); anchors.SetActive(false);
                Assert.IsEmpty(adapter.Manifest.Roles); Assert.IsEmpty(adapter.Manifest.Locations);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
        [Test] public void MaterialDefaultHydrationKeepsIdentityButVisualChangesInvalidateIt()
        {
            var material = new Material(Shader.Find("Skybox/Procedural"));
            try
            {
                var serialized = new SerializedObject(material);
                serialized.FindProperty("m_SavedProperties.m_Floats").ClearArray();
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var method = typeof(GameDirectorAdapterBase).GetMethod("AppendMaterialIdentity", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                string Identity() { var value = new System.Text.StringBuilder(); method.Invoke(null, new object[] { value, material }); return value.ToString(); }
                var before = Identity(); var value = material.GetFloat("_SunSizeConvergence");
                material.SetFloat("_SunSizeConvergence", value);
                Assert.AreEqual(before, Identity(), "Writing an already-effective shader default is not a new visual source.");
                material.SetFloat("_SunSizeConvergence", value + 1);
                Assert.AreNotEqual(before, Identity(), "Actual shader parameter changes must invalidate cached pictures.");
            }
            finally { Object.DestroyImmediate(material); }
        }
        [Test] public void InventoryUsesStableSubAssetIdsAndKeepsDuration()
        {
            var dir = "Assets/GDIndexTest_" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", dir.Substring(7));
            try
            {
                var first = new AnimationClip { name = "wave" }; first.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
                var path = dir + "/clips.asset"; AssetDatabase.CreateAsset(first, path);
                AssetDatabase.AddObjectToAsset(new AnimationClip { name = "nod" }, path); AssetDatabase.SaveAssetIfDirty(first);
                var before = DirectorAssetIndex.Read(path).ToArray();
                Assert.AreEqual(2, before.Length); Assert.AreNotEqual(before[0].id, before[1].id);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(first, out string guid, out long localId);
                Assert.AreEqual(1, before.Single(e => e.id == "unity:" + guid + ":" + localId).duration);
                AssetDatabase.MoveAsset(path, dir + "/renamed.asset");
                CollectionAssert.AreEquivalent(before.Select(e => e.id), DirectorAssetIndex.Read(dir + "/renamed.asset").Select(e => e.id));
            }
            finally { AssetDatabase.DeleteAsset(dir); }
        }
        [Test] public void NewStagePreservesOpenSceneAndDiscoversOnlyBoundControllerStates()
        {
            // The guarded EditMode runner owns this empty scene. Never replace
            // an interactive scene if this test is invoked outside that fixture.
            var testScene = SceneManager.GetActiveScene();
            var defaultBatchFixture = Application.isBatchMode && testScene.GetRootGameObjects().All(r => r.GetComponent<Camera>() != null || r.GetComponent<Light>() != null);
            if (SceneManager.sceneCount != 1 || !testScene.IsValid() || !testScene.isLoaded || EditorSceneManager.IsPreviewScene(testScene) ||
                !string.IsNullOrEmpty(testScene.path) || testScene.isDirty || (testScene.rootCount != 0 && !defaultBatchFixture))
                Assert.Ignore("Requires the test runner's single empty, untitled fixture scene.");
            var dir = "Assets/GDStageTest_" + System.Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", dir.Substring(7));
            var original = testScene;
            var originalDirty = original.isDirty;
            var preview = EditorSceneManager.NewPreviewScene(); string stagePath = null; Scene stage = default;
            try
            {
                var source = new GameObject("actor"); SceneManager.MoveGameObjectToScene(source, preview);
                var controller = AnimatorController.CreateAnimatorControllerAtPath(dir + "/actor.controller");
                var clip = new AnimationClip { name = "greeting" }; AssetDatabase.CreateAsset(clip, dir + "/greeting.anim");
                controller.AddMotion(clip); controller.layers[0].stateMachine.AddState("unbound");
                source.AddComponent<Animator>().runtimeAnimatorController = controller;
                source.AddComponent<GameplayProbe>();
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, dir + "/actor.prefab");

                var scenesBefore = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).OrderBy(id => id).ToArray();
                var foldersBefore = AssetDatabase.GetAllAssetPaths().Where(AssetDatabase.IsValidFolder).OrderBy(p => p).ToArray();
                Assert.Throws<System.InvalidOperationException>(() => DirectorStageFactory.Create(prefab));
                Assert.AreEqual(original, SceneManager.GetActiveScene()); Assert.AreEqual(originalDirty, original.isDirty);
                CollectionAssert.AreEqual(scenesBefore, AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).OrderBy(id => id).ToArray());
                CollectionAssert.AreEqual(foldersBefore, AssetDatabase.GetAllAssetPaths().Where(AssetDatabase.IsValidFolder).OrderBy(p => p).ToArray());

                // Name only the runner-owned fixture, then leave an unsaved edit
                // in it. The production action must preserve that edit and disk.
                Assert.IsTrue(EditorSceneManager.SaveScene(original, dir + "/open-scene.unity"));
                var savedBytes = System.IO.File.ReadAllBytes(dir + "/open-scene.unity");
                var sentinel = new GameObject("unsaved user edit");
                EditorSceneManager.MarkSceneDirty(original); originalDirty = true;
                stagePath = DirectorStageFactory.Create(prefab);
                Assert.AreEqual(original, SceneManager.GetActiveScene()); Assert.AreEqual(originalDirty, original.isDirty);
                Assert.AreEqual(original, sentinel.scene);
                CollectionAssert.AreEqual(savedBytes, System.IO.File.ReadAllBytes(dir + "/open-scene.unity"));
                Assert.IsNotNull(prefab.GetComponent<GameplayProbe>());
                stage = EditorSceneManager.OpenScene(stagePath, OpenSceneMode.Additive);
                var adapter = stage.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<GenericSceneAdapter>()).Single();
                var manifest = adapter.Manifest;
                Assert.AreEqual("offline-sandbox", manifest.Capabilities["director.mode"]);
                Assert.IsTrue(manifest.FindRole("lead").PresentAtStart);
                CollectionAssert.AreEqual(new[] { "Base Layer.greeting" }, manifest.FindActor("lead-actor").Clips);
                Assert.AreEqual(3, manifest.Locations.Count);
                Assert.IsFalse(stage.GetRootGameObjects().Any(r => r.GetComponent<GameplayProbe>() != null));
            }
            finally
            {
                if (stage.IsValid() && stage.isLoaded) EditorSceneManager.CloseScene(stage, true);
                EditorSceneManager.ClosePreviewScene(preview);
                // Discard only the test-owned fixture; the guarded runner restores
                // the user's pre-test scene setup after the suite finishes.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (stagePath != null) AssetDatabase.DeleteAsset(stagePath);
                AssetDatabase.DeleteAsset(dir);
            }
        }
    }
}
