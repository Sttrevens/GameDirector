using System;
using System.IO;
using System.Linq;
using GameDirector.Unity.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.Unity.Tests
{
    public sealed class FilmAuthoringTests
    {
        [Test] public void RenameKeepsDocumentIdentityAndCopyCreatesANewDocument()
        {
            var folder = "Assets/FilmIdentity_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            try
            {
                var asset = ScriptableObject.CreateInstance<DirectorFilmAsset>();
                AssetDatabase.CreateAsset(asset, folder + "/Original.asset");
                var id = asset.DocumentId;
                Assert.AreEqual("", AssetDatabase.MoveAsset(folder + "/Original.asset", folder + "/Renamed.asset"));
                Assert.AreEqual(id, asset.DocumentId);
                Assert.IsTrue(AssetDatabase.CopyAsset(folder + "/Renamed.asset", folder + "/Copy.asset"));
                var copy = AssetDatabase.LoadAssetAtPath<DirectorFilmAsset>(folder + "/Copy.asset");
                Assert.AreNotEqual(id, copy.DocumentId);
                var editor = UnityEditor.Editor.CreateEditor(asset);
                Assert.IsInstanceOf<DirectorFilmAssetEditor>(editor);
                UnityEngine.Object.DestroyImmediate(editor);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
        [Test] public void ExampleBindsAnActualAnimatorStateAndPreservesTheOpenScene()
        {
            var original = SceneManager.GetActiveScene();
            var emptyFixture = Application.isBatchMode && SceneManager.sceneCount == 1 && string.IsNullOrEmpty(original.path) && !original.isDirty &&
                original.GetRootGameObjects().All(r => r.GetComponent<Camera>() != null || r.GetComponent<Light>() != null);
            if (!emptyFixture) Assert.Ignore("Requires the runner-owned empty fixture.");
            var before = AssetDatabase.GetAllAssetPaths().OrderBy(p => p).ToArray();
            Assert.Throws<InvalidOperationException>(() => DirectorExample.CreateAsset());
            CollectionAssert.AreEqual(before, AssetDatabase.GetAllAssetPaths().OrderBy(p => p).ToArray());
            var folder = "Assets/FilmExampleTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            DirectorFilmAsset asset = null; Scene stage = default; string sampleFolder = null, stagePath = null;
            try
            {
                EditorSceneManager.SaveScene(original, folder + "/Original.unity");
                var bytes = File.ReadAllBytes(folder + "/Original.unity");
                var unsaved = new GameObject("unsaved authoring"); EditorSceneManager.MarkSceneDirty(original);
                asset = DirectorExample.CreateAsset();
                sampleFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(asset)); stagePath = AssetDatabase.GetAssetPath(asset.stage);
                Assert.AreEqual(original, SceneManager.GetActiveScene()); Assert.IsTrue(original.isDirty); Assert.AreEqual(original, unsaved.scene);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(folder + "/Original.unity"));
                Assert.IsNotNull(asset.suppliedSound);
                Assert.AreEqual(3, asset.Film["scenes"][0]["shots"].Count());
                stage = EditorSceneManager.OpenScene(stagePath, OpenSceneMode.Additive);
                var manifest = stage.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<GenericSceneAdapter>()).Single().Manifest;
                Assert.IsTrue(stage.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Animator>()).All(a => !a.enabled),
                    "Owned stage animations must not run on the Unity wall clock between director sessions.");
                CollectionAssert.Contains(manifest.FindActor("lead-actor").Clips, (string)asset.Film["scenes"][0]["performance"][0]["clip"]);
            }
            finally
            {
                if (stage.IsValid() && stage.isLoaded) EditorSceneManager.CloseScene(stage, true);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (stagePath != null) AssetDatabase.DeleteAsset(stagePath);
                if (sampleFolder != null) AssetDatabase.DeleteAsset(sampleFolder);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
