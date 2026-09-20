using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    public static class DirectorWorkbenchLauncher
    {
        private static readonly SemaphoreSlim starting = new SemaphoreSlim(1, 1);
        public static string RuntimeFolder
        {
            get => EditorPrefs.GetString("GameDirector.RuntimeFolder", "");
            set => EditorPrefs.SetString("GameDirector.RuntimeFolder", value);
        }
        private static string Executable(string root) => Path.Combine(root, Application.platform == RuntimePlatform.WindowsEditor ? "gamedirector-workbench.exe" : "gamedirector-workbench");
        public static async Task EnsureStarted(CancellationToken ct = default)
        {
            await starting.WaitAsync(ct);
            try
            {
                if (await Healthy(ct)) return;
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDirector", "Workbench");
                var descriptor = Path.Combine(data, ".service.json");
                if (await Discover(descriptor, ct)) return;
                var root = RuntimeFolder;
                if (!File.Exists(Executable(root)))
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DirectorWorkbenchLauncher).Assembly);
                    if (package != null) root = Path.Combine(package.resolvedPath, "Tools~", "Workbench");
                }
                if (!File.Exists(Executable(root)))
                {
                    root = EditorUtility.OpenFolderPanel("选择 GameDirector 运行组件的 workbench 文件夹", RuntimeFolder, "");
                    if (root == "") throw new OperationCanceledException();
                    if (!File.Exists(Executable(root))) throw new InvalidOperationException("该文件夹没有独立工作台程序。请选择本平台发行包中的 workbench 文件夹。");
                    RuntimeFolder = root;
                }
                Directory.CreateDirectory(Path.Combine(data, "logs"));
                var logPath = Path.Combine(data, "logs", "launcher-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
                var info = new ProcessStartInfo(Executable(root)) { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = root, Arguments = "--port 0 --data " + Quote(data), RedirectStandardOutput = true, RedirectStandardError = true };
                var media = Path.GetFullPath(Path.Combine(root, "..", "media"));
                foreach (var name in new[] { "ffmpeg", "ffprobe" })
                {
                    var tool = Path.Combine(media, name + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : ""));
                    if (File.Exists(tool)) info.EnvironmentVariables[name == "ffmpeg" ? "GAMEDIRECTOR_FFMPEG" : "GAMEDIRECTOR_FFPROBE"] = tool;
                }
                var process = new Process { StartInfo = info, EnableRaisingEvents = true };
                var logGate = new object();
                DataReceivedEventHandler log = (_, e) => { if (e.Data != null) { lock (logGate) { try { File.AppendAllText(logPath, e.Data + Environment.NewLine); } catch (IOException) { } } } };
                process.OutputDataReceived += log; process.ErrorDataReceived += log;
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                // The service owns its lifetime; closing an Inspector does not stop jobs.
                for (var i = 0; i < 60; i++)
                {
                    if (await Discover(descriptor, ct)) return;
                    if (process.HasExited) throw new InvalidOperationException("工作台启动失败，请查看 " + logPath);
                    await Task.Delay(250, ct);
                }
                throw new InvalidOperationException("工作台仍未就绪。重试连接会先检查现有实例。日志：" + logPath);
            }
            finally { starting.Release(); }
        }
        private static async Task<bool> Healthy(CancellationToken ct)
        {
            try { var health = await DirectorWorkbenchClient.Call("health", null, ct); return (string)health["product"] == "GameDirector" && (int?)health["protocolVersion"] == 1; }
            catch (Exception) when (!ct.IsCancellationRequested) { return false; }
        }
        private static async Task<bool> Discover(string descriptor, CancellationToken ct)
        {
            if (!File.Exists(descriptor)) return false;
            var previous = DirectorWorkbenchClient.Address;
            try
            {
                var d = JObject.Parse(File.ReadAllText(descriptor)); DirectorWorkbenchClient.Address = (string)d["address"];
                var health = await DirectorWorkbenchClient.Call("health", null, ct);
                if ((string)health["product"] == "GameDirector" && (int?)health["protocolVersion"] == 1 && (string)health["instanceId"] == (string)d["instanceId"]) return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            DirectorWorkbenchClient.Address = previous; return false;
        }
        private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
