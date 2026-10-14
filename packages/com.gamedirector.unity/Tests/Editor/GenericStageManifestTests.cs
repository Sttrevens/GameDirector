using System.Reflection;
using GameDirector.Core.Dsl;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.Unity.Tests
{
    /// <summary>The generic stage's manifest, not a private game location table,
    /// must drive its advertised geometry. Exercise arbitrary names/coordinates
    /// and an authored manifest so a fixed vocabulary/table cannot satisfy this.</summary>
    public class GenericStageManifestTests
    {
        [Test] public void GenericStageReadsAuthoredManifestAndDiscoversCurrentRegistryGeometry()
        {
            var scene = EditorSceneManager.NewPreviewScene(); TextAsset authored = null;
            try
            {
                var root = new GameObject("Generic stage"); SceneManager.MoveGameObjectToScene(root, scene);
                var registry = root.AddComponent<DirectorLocationRegistry>();
                var anchor = new GameObject("arbitrary-user-anchor").transform; anchor.SetParent(root.transform); anchor.position = new Vector3(13, 7, -5);
                var adapter = root.AddComponent<GenericSceneAdapter>();
                CapabilityManifest Read() => (CapabilityManifest)typeof(GenericSceneAdapter).GetMethod("BuildManifest", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, null);
                var discovered = Read();
                Assert.AreEqual(1, discovered.Locations.Count);
                CollectionAssert.AreEqual(new[] { 13f, 7f, -5f }, discovered.FindLocation(anchor.name).Position);
                anchor.name = "renamed-user-anchor"; anchor.position = new Vector3(-4, 2, 19);
                discovered = Read(); Assert.IsNull(discovered.FindLocation("arbitrary-user-anchor"));
                CollectionAssert.AreEqual(new[] { -4f, 2f, 19f }, discovered.FindLocation(anchor.name).Position);
                var manifest = new CapabilityManifest { Game = "Authored generic stage" };
                manifest.Locations.Add(new LocationDescriptor { Id = "manifest-only-anchor", Position = new[] { 9f, 11f, 15f } });
                authored = new TextAsset(JsonConvert.SerializeObject(manifest));
                var serialized = new SerializedObject(adapter); serialized.FindProperty("manifestJson").objectReferenceValue = authored; serialized.ApplyModifiedPropertiesWithoutUndo();
                var loaded = Read(); Assert.AreEqual(manifest.Game, loaded.Game); Assert.AreEqual(1, loaded.Locations.Count);
                CollectionAssert.AreEqual(new[] { 9f, 11f, 15f }, loaded.FindLocation("manifest-only-anchor").Position);
                Assert.IsNull(loaded.FindLocation("renamed-user-anchor"));
            }
            finally { if (authored != null) Object.DestroyImmediate(authored); EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
