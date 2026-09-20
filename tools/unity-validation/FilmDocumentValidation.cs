// Test harness copied only into a runner-owned isolated Unity project.
using System;
using System.IO;
using System.Threading.Tasks;
using GameDirector.Unity;
using GameDirector.Unity.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class FilmDocumentValidation
{
    private const string Active = "GameDirector.FilmDocumentValidation";
    private static string Evidence => Environment.GetEnvironmentVariable("GD_VALIDATION_EVIDENCE");
    static FilmDocumentValidation()
    {
        EditorApplication.playModeStateChanged += Changed;
        EditorApplication.update += Tick;
    }
    public static void Run()
    {
        if (!Application.isBatchMode || string.IsNullOrEmpty(Evidence) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".gamedirector-validation-project")))
            throw new InvalidOperationException("Requires an owned isolated batch validation project.");
        Directory.CreateDirectory(Evidence);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/DocumentValidationSource.unity");
        var asset = DirectorExample.CreateAsset();
        SessionState.SetString(Active + ".asset", AssetDatabase.GetAssetPath(asset));
        var stage = EditorSceneManager.OpenScene(AssetDatabase.GetAssetPath(asset.stage), OpenSceneMode.Single);
        var bridge = UnityEngine.Object.FindObjectOfType<DirectorBridgeServer>();
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("port").intValue = int.Parse(Environment.GetEnvironmentVariable("GD_VALIDATION_BRIDGE_PORT"));
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.SaveScene(stage);
        SessionState.SetBool(Active, true);
        EditorApplication.isPlaying = true;
    }
    private static async void Changed(PlayModeStateChange change)
    {
        if (!SessionState.GetBool(Active, false)) return;
        if (change == PlayModeStateChange.EnteredPlayMode)
        {
            try { await Prepare(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Evidence, "failure.txt"), ex.ToString()); EditorApplication.isPlaying = false; }
        }
        if (change == PlayModeStateChange.EnteredEditMode)
        {
            SessionState.SetBool(Active, false);
            EditorApplication.Exit(File.Exists(Path.Combine(Evidence, "failure.txt")) ? 1 : 0);
        }
    }
    private static async Task Prepare()
    {
        var asset = AssetDatabase.LoadAssetAtPath<DirectorFilmAsset>(SessionState.GetString(Active + ".asset", ""));
        await DirectorWorkbenchClient.Register();
        await DirectorWorkbenchClient.Call("projects/" + DirectorWorkbenchClient.ProjectId + "/connect", new { });
        var path = AssetDatabase.GetAssetPath(asset.suppliedSound);
        var sound = await DirectorWorkbenchClient.Call("projects/" + DirectorWorkbenchClient.ProjectId + "/media",
            new { name = Path.GetFileName(path), base64 = Convert.ToBase64String(File.ReadAllBytes(path)) });
        var film = asset.Film;
        film["width"] = 640; film["height"] = 360; film["frameRate"] = 12;
        ((JArray)film["audio"]).Add(new JObject { ["id"] = "example-music", ["mediaId"] = sound["id"], ["bus"] = "music",
            ["at"] = 0, ["sourceStart"] = 0, ["duration"] = 3, ["volume"] = .7, ["fadeIn"] = 0, ["fadeOut"] = 0 });
        asset.Film = film; EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset);
        var document = await DirectorWorkbenchClient.Operation(DirectorWorkbenchClient.DocumentPath(asset),
            new JObject { ["expectedRevision"] = 0, ["film"] = film });
        var readiness = await DirectorWorkbenchClient.Call(DirectorWorkbenchClient.DocumentPath(asset) + "/readiness", new { revision = 1 });
        File.WriteAllText(Path.Combine(Evidence, "ready.json"), new JObject {
            ["projectId"] = DirectorWorkbenchClient.ProjectId, ["documentId"] = asset.DocumentId,
            ["assetPath"] = AssetDatabase.GetAssetPath(asset), ["document"] = document,
            ["readiness"] = readiness, ["sourceRoot"] = Path.GetDirectoryName(Application.dataPath)
        }.ToString());
    }
    private static void Tick()
    {
        if (SessionState.GetBool(Active, false) && !string.IsNullOrEmpty(Evidence) && EditorApplication.isPlaying &&
            File.Exists(Path.Combine(Evidence, "stop"))) EditorApplication.isPlaying = false;
    }
}
