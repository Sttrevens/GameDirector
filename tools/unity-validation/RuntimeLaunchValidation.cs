// Copied only into an owned validation project; never shipped in the package.
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameDirector.Unity.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class RuntimeLaunchValidation
{
    public static async void Run()
    {
        var evidence = Environment.GetEnvironmentVariable("GD_VALIDATION_EVIDENCE");
        if (!Application.isBatchMode || string.IsNullOrEmpty(evidence) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".gamedirector-validation-project")))
            throw new InvalidOperationException("Requires an owned isolated validation project.");
        var key = "GameDirector.WorkbenchAddress";
        var existed = EditorPrefs.HasKey(key);
        var previous = EditorPrefs.GetString(key, "");
        var code = 1;
        try
        {
            Directory.CreateDirectory(evidence);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DirectorWorkbenchLauncher).Assembly);
            var runtime = Path.Combine(package.resolvedPath, "Tools~", "Workbench");
            var type = typeof(DirectorWorkbenchLauncher);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var manifest = type.GetMethod("ValidateRuntimeManifest", flags).Invoke(null, new object[] { runtime, true });
            runtime = (string)type.GetMethod("PrepareExecutable", flags).Invoke(null, new object[] { runtime, manifest });
            // Use the production launch implementation with an owned data store.
            await (Task)type.GetMethod("StartFrom", flags).Invoke(null, new object[] { runtime, Path.Combine(evidence, "store"), CancellationToken.None });
            var health = await DirectorWorkbenchClient.Call("health");
            if ((string)health["product"] != "GameDirector") throw new InvalidOperationException("Unexpected runtime product.");
            File.WriteAllText(Path.Combine(evidence, "launcher-result.json"), new JObject {
                ["passed"] = (string)health["product"] == "GameDirector",
                ["rid"] = DirectorWorkbenchLauncher.CurrentRid(), ["health"] = health,
                ["runtime"] = runtime
            }.ToString());
            code = 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(evidence, "launcher-failure.txt"), ex.ToString()); }
        finally
        {
            if (existed) EditorPrefs.SetString(key, previous); else EditorPrefs.DeleteKey(key);
            EditorApplication.Exit(code);
        }
    }
}
