// Batch-only probe copied into an owned scratch project, not shipped.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

[InitializeOnLoad]
public static class QuarantineValidation
{
    private static readonly string Evidence = Environment.GetEnvironmentVariable("GD_QUARANTINE_EVIDENCE");
    private const string Pending = "GDQuarantinePendingAdd";
    static QuarantineValidation()
    {
        if (Application.isBatchMode && !string.IsNullOrEmpty(Evidence) && SessionState.GetBool(Pending, false))
            EditorApplication.delayCall += ResumeAdd;
    }
    private static void Guard()
    {
        if (!Application.isBatchMode || string.IsNullOrEmpty(Evidence) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".gamedirector-validation-project")))
            throw new InvalidOperationException("Requires owned quarantine validation project.");
    }
    public static void Add()
    {
        Guard(); SessionState.SetBool(Pending, true);
        var request = Client.Add("file:" + Environment.GetEnvironmentVariable("GD_QUARANTINE_TGZ"));
        EditorApplication.CallbackFunction poll = null;
        poll = () => {
            if (!request.IsCompleted) return;
            EditorApplication.update -= poll;
            if (request.Status == StatusCode.Success) FinishAdd(request.Result.resolvedPath);
            else { File.WriteAllText(Path.Combine(Evidence, "add-failure.txt"), request.Error.message); EditorApplication.Exit(1); }
        };
        EditorApplication.update += poll;
    }
    private static void ResumeAdd()
    {
        var request = Client.List(true);
        EditorApplication.CallbackFunction poll = null;
        poll = () => {
            if (!request.IsCompleted) return;
            EditorApplication.update -= poll;
            var package = request.Result?.FirstOrDefault(p => p.name == "com.gamedirector.unity");
            if (package != null) FinishAdd(package.resolvedPath);
            else { File.WriteAllText(Path.Combine(Evidence, "add-failure.txt"), "Local package add did not resolve."); EditorApplication.Exit(1); }
        };
        EditorApplication.update += poll;
    }
    private static void FinishAdd(string path)
    {
        SessionState.SetBool(Pending, false);
        File.WriteAllText(Path.Combine(Evidence, "package-path.txt"), path); EditorApplication.Exit(0);
    }
    [Serializable] private class Result { public bool passed; public string cli; public int exit = -1; public string exception; }
    public static void Launch()
    {
        Guard(); var result = new Result();
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("GameDirector.Unity.Editor.DirectorWorkbenchLauncher")).First(t => t != null);
            result.cli = (string)type.GetMethod("ResolveBundledCli", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            // Exactly the command resolution Copy MCP Config uses, then an actual native launch.
            using var p = Process.Start(new ProcessStartInfo(result.cli, "--help") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (!p.WaitForExit(30000)) { p.Kill(); p.WaitForExit(); throw new TimeoutException("Bundled gd launch"); }
            File.WriteAllText(Path.Combine(Evidence, "gd.stdout"), p.StandardOutput.ReadToEnd());
            File.WriteAllText(Path.Combine(Evidence, "gd.stderr"), p.StandardError.ReadToEnd());
            result.exit = p.ExitCode; result.passed = p.ExitCode == 0;
        }
        catch (Exception ex) { result.exception = ex.ToString(); }
        finally { File.WriteAllText(Path.Combine(Evidence, "launch.json"), JsonUtility.ToJson(result, true)); EditorApplication.Exit(result.passed ? 0 : 1); }
    }
}
