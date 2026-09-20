// Copied ONLY into a newly created, disposable validation project by the runner.
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using GameDirector.Unity;
using GameDirector.Unity.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class CaptureValidation
{
    private const string Session = "GameDirector.OwnedCaptureValidation";
    static CaptureValidation()
    {
        EditorApplication.playModeStateChanged += Changed;
        EditorApplication.update += Tick;
    }
    private static string Evidence => Environment.GetEnvironmentVariable("GD_VALIDATION_EVIDENCE");
    public static void Run()
    {
        var marker = Path.Combine(Path.GetDirectoryName(Application.dataPath), ".gamedirector-validation-project");
        if (!Application.isBatchMode || !File.Exists(marker) || string.IsNullOrEmpty(Evidence))
            throw new InvalidOperationException("Requires a runner-owned isolated validation project and evidence directory.");
        Directory.CreateDirectory(Evidence);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/ValidationSource.unity");
        var actor = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        actor.name = "Validation actor";
        var material = new Material(Shader.Find("Standard")); material.color = new Color(.9f, .25f, .12f);
        AssetDatabase.CreateAsset(material, "Assets/ValidationMaterial.mat");
        actor.GetComponent<Renderer>().sharedMaterial = material;
        var prefab = PrefabUtility.SaveAsPrefabAsset(actor, "Assets/ValidationActor.prefab");
        UnityEngine.Object.DestroyImmediate(actor);
        var stagePath = DirectorStageFactory.Create(prefab);
        var stage = EditorSceneManager.OpenScene(stagePath, OpenSceneMode.Single);
        var bridge = UnityEngine.Object.FindObjectOfType<DirectorBridgeServer>();
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("port").intValue = 39779;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.SaveScene(stage);
        AssetDatabase.SaveAssets();
        SessionState.SetBool(Session, true);
        EditorApplication.isPlaying = true;
    }
    private static void Changed(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Session, false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
            { Dump("before"); File.WriteAllText(Path.Combine(Evidence, "ready"), Application.dataPath); }
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            SessionState.SetBool(Session, false);
            EditorApplication.Exit(0);
        }
    }
    private static void Dump(string suffix)
    {
        var adapter = UnityEngine.Object.FindObjectOfType<GenericSceneAdapter>();
        var method = typeof(GameDirectorAdapterBase).GetMethod("BuildSourceIdentity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        File.WriteAllText(Path.Combine(Evidence, "identity-" + suffix + ".txt"), (string)method.Invoke(adapter, null));
        var lines = new List<string>();
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                foreach (var component in t.GetComponents<Component>()) if (component != null)
                {
                    lines.Add(t.name + " | " + component.GetType().FullName + " | " + EditorJsonUtility.ToJson(component));
                    if (component is Renderer r) foreach (var material in r.sharedMaterials)
                        if (material != null) lines.Add("Material | " + EditorJsonUtility.ToJson(material));
                }
        File.WriteAllLines(Path.Combine(Evidence, "live-" + suffix + ".txt"), lines);
    }
    private static void Tick()
    {
        if (SessionState.GetBool(Session, false) && EditorApplication.isPlaying &&
            File.Exists(Path.Combine(Evidence, "stop"))) { Dump("after"); EditorApplication.isPlaying = false; }
    }
}
