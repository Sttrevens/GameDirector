using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    /// <summary>Launch the workbench runtime shipped inside the full UPM package
    /// (Tools~/Workbench). Validates platform/protocol compatibility against the
    /// bundled gamedirector.runtime.json, repairs Unix executable permissions
    /// lost by cross-built archives, and never writes into PackageCache — when
    /// the package lives there and needs chmod, the runtime is mirrored into the
    /// per-user data folder first. A source-only (Git UPM) install carries no
    /// runtime and gets a clear, actionable message.</summary>
    public static class DirectorWorkbenchLauncher
    {
        private static readonly SemaphoreSlim starting = new SemaphoreSlim(1, 1);
        public static string RuntimeFolder
        {
            get => EditorPrefs.GetString("GameDirector.RuntimeFolder", "");
            set => EditorPrefs.SetString("GameDirector.RuntimeFolder", value);
        }
        private static string Executable(string root) => Path.Combine(root, Application.platform == RuntimePlatform.WindowsEditor ? "gamedirector-workbench.exe" : "gamedirector-workbench");

        /// <summary>The runtime identifier this Unity editor needs, matching the
        /// distribution manifest's platform names.</summary>
        public static string CurrentRid()
        {
            string platform;
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: platform = "win"; break;
                case RuntimePlatform.OSXEditor: platform = "osx"; break;
                case RuntimePlatform.LinuxEditor: platform = "linux"; break;
                default: platform = "unknown"; break;
            }
            return platform + "-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        }

        private const int RequiredProtocol = 1;

        private static bool IsPackageCache(string path) => path.Replace('\\', '/').Contains("/PackageCache/");

        /// <summary>Validate the bundled runtime's manifest against this editor.
        /// Returns the manifest when compatible; throws with an actionable
        /// message otherwise. A null manifest path means "no manifest shipped":
        /// only acceptable for a user-chosen legacy runtime folder.</summary>
        private static JObject ValidateRuntimeManifest(string root, bool fromPackage)
        {
            var manifestPath = Path.Combine(root, "gamedirector.runtime.json");
            if (!File.Exists(manifestPath))
            {
                if (fromPackage)
                    throw new InvalidOperationException(
                        "此 GameDirector 包没有随附本平台的工作台运行组件（缺少 Tools~/Workbench/gamedirector.runtime.json），" +
                        "通常是源码方式（Git URL）安装的精简包。请改用完整平台 UPM 包：Unity Package Manager → Add package from tarball → " +
                        "com.gamedirector.unity-<版本>-" + CurrentRid() + ".tgz（由 GameDirector 发行流程产出，请向提供方索取）。");
                return null;
            }
            JObject manifest;
            try { manifest = JObject.Parse(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { throw new InvalidOperationException("运行组件清单无法解析：" + manifestPath + " — " + ex.Message); }
            var rid = (string)manifest["rid"];
            var protocol = (int?)manifest["protocolVersion"] ?? 0;
            if ((string)manifest["product"] != "GameDirector")
                throw new InvalidOperationException("运行组件清单不是 GameDirector 产物：" + manifestPath);
            if (rid != CurrentRid())
                throw new InvalidOperationException(
                    "随包工作台运行组件面向 " + rid + "，但当前 Unity 编辑器是 " + CurrentRid() +
                    "。请安装面向本平台的完整 UPM 包（com.gamedirector.unity-<版本>-" + CurrentRid() + ".tgz）。");
            if (protocol != RequiredProtocol)
                throw new InvalidOperationException(
                    "随包工作台协议版本 " + protocol + " 与本编辑器插件要求的 " + RequiredProtocol +
                    " 不兼容。请升级或降级到相互匹配的同版本包。");
            return manifest;
        }

        /// <summary>Declared-path safety: every sha256 key and executable name in
        /// the runtime manifest must resolve to a path below the runtime folder.</summary>
        private static bool TryResolveBelow(string root, string relative, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) return false;
            foreach (var segment in relative.Replace('\\', '/').Split('/'))
                if (segment == ".." || segment.Length == 0) return false;
            full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        /// <summary>Hash-verify every file the runtime manifest declares, and confirm
        /// declared executable paths stay below the runtime folder. A manifest
        /// must include its entry executable in the hash map.</summary>
        private static bool VerifyDeclaredHashes(string root, JObject runtime, out string error)
        {
            if (runtime == null) { error = null; return true; }
            var map = runtime["sha256"] as JObject;
            if (map == null || map[Path.GetFileName(Executable(root))] == null)
            { error = "运行组件清单缺少程序的校验值，请重新安装完整平台包。"; return false; }
            if (map != null)
            {
                foreach (var prop in map.Properties())
                {
                    if (!TryResolveBelow(root, prop.Name, out var full)) { error = "清单声明了越界路径：" + prop.Name; return false; }
                    if (!File.Exists(full)) { error = "缺少清单声明的文件：" + prop.Name; return false; }
                    string actual;
                    try
                    {
                        using (var stream = File.OpenRead(full))
                        using (var sha = SHA256.Create())
                            actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    }
                    catch (IOException ex) { error = "无法读取：" + prop.Name + "（" + ex.Message + "）"; return false; }
                    catch (UnauthorizedAccessException ex) { error = "无法读取：" + prop.Name + "（" + ex.Message + "）"; return false; }
                    if (!string.Equals(actual, (string)prop.Value, StringComparison.OrdinalIgnoreCase)) { error = "文件被改动或损坏：" + prop.Name; return false; }
                }
            }
            var executables = runtime["executables"] as JArray;
            if (executables != null)
                foreach (var name in executables.Values<string>())
                    if (!TryResolveBelow(root, name, out _)) { error = "清单声明了越界的可执行文件路径：" + name; return false; }
            error = null;
            return true;
        }

        /// <summary>Fail honestly when a manifest-carrying runtime was modified or
        /// corrupted; legacy folders without a manifest keep their previous behavior.</summary>
        private static void VerifyRuntimeIntegrity(string root, JObject runtime)
        {
            if (runtime == null) return;
            if (!VerifyDeclaredHashes(root, runtime, out var error))
                throw new InvalidOperationException("工作台运行组件未通过完整性校验：" + error + "。请重新安装完整的 GameDirector 平台包。");
        }

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
                    if (package != null)
                    {
                        var bundled = Path.Combine(package.resolvedPath, "Tools~", "Workbench");
                        if (File.Exists(Executable(bundled)) || File.Exists(Path.Combine(bundled, "gamedirector.runtime.json")))
                        {
                            // Full platform package: validate compatibility first —
                            // a platform or protocol mismatch must fail with words,
                            // not a hang. Then prove the declared file hashes before
                            // the executable or any mirror of it is trusted. Only
                            // then repair Unix exec bits if needed.
                            var runtime = ValidateRuntimeManifest(bundled, fromPackage: true);
                            VerifyRuntimeIntegrity(bundled, runtime);
                            root = PrepareExecutable(bundled, runtime);
                            await StartFrom(root, data, ct);
                            return;
                        }
                        // Package present but carries no runtime: a source-only
                        // (Git URL) install. Explain, then still allow picking a
                        // runtime folder from a full distribution by hand.
                        EditorUtility.DisplayDialog("GameDirector 工作台",
                            "此来源安装的 GameDirector 包不含工作台运行组件（精简源码包）。\n\n" +
                            "推荐：改用完整平台 UPM 包 — Unity Package Manager → Add package from tarball → " +
                            "com.gamedirector.unity-<版本>-" + CurrentRid() + ".tgz（由 GameDirector 发行流程产出，请向包提供方索取）。\n\n" +
                            "或：在接下来打开的文件夹选择器中，手动指向完整发行版里的 workbench 文件夹。",
                            "了解");
                    }
                }
                else
                {
                    // A previously configured runtime folder: validate when it
                    // carries a manifest (legacy folders without one still launch).
                    var runtime = ValidateRuntimeManifest(root, fromPackage: false);
                    VerifyRuntimeIntegrity(root, runtime);
                    root = PrepareExecutable(root, runtime);
                    await StartFrom(root, data, ct);
                    return;
                }
                var picked = EditorUtility.OpenFolderPanel("选择 GameDirector 运行组件的 workbench 文件夹", RuntimeFolder, "");
                if (picked == "") throw new OperationCanceledException();
                if (!File.Exists(Executable(picked))) throw new InvalidOperationException("该文件夹没有独立工作台程序。请选择本平台发行包中的 workbench 文件夹。");
                var pickedRuntime = ValidateRuntimeManifest(picked, fromPackage: false);
                VerifyRuntimeIntegrity(picked, pickedRuntime);
                RuntimeFolder = picked;
                await StartFrom(PrepareExecutable(picked, pickedRuntime), data, ct);
            }
            finally { starting.Release(); }
        }

        private static async Task StartFrom(string root, string data, CancellationToken ct)
        {
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
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException("无法启动工作台程序 " + Executable(root) + "：" + ex.Message + "。日志：" + logPath, ex);
            }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            // The service owns its lifetime; closing an Inspector does not stop jobs.
            for (var i = 0; i < 60; i++)
            {
                if (await Discover(Path.Combine(data, ".service.json"), ct)) return;
                if (process.HasExited) throw new InvalidOperationException("工作台启动失败，请查看 " + logPath);
                await Task.Delay(250, ct);
            }
            throw new InvalidOperationException("工作台仍未就绪。重试连接会先检查现有实例。日志：" + logPath);
        }

        /// <summary>Unix executables arriving from a cross-built archive (the tgz
        /// was staged on another OS) may lack the execute bit. Repairs it in place
        /// for embedded packages, or via a per-user mirror when the package lives
        /// in PackageCache — which is never written. Returns the folder to launch.</summary>
        private static string PrepareExecutable(string root, JObject runtime)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) return root;
            var exe = Executable(root);
            if (!File.Exists(exe)) return root;
            if (HasExecuteBit(exe)) return root;
            var launch = root;
            if (IsPackageCache(root))
            {
                // Content-addressed mirror in the per-user data folder; PackageCache
                // itself stays untouched.
                var key = "runtime";
                try
                {
                    var manifestPath = Path.Combine(root, "gamedirector.runtime.json");
                    if (File.Exists(manifestPath))
                    {
                        using var sha = SHA256.Create();
                        key = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(manifestPath))).Replace("-", "").ToLowerInvariant();
                    }
                }
                catch (IOException) { }
                launch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDirector", "RuntimeMirror", key);
                Directory.CreateDirectory(Path.GetDirectoryName(launch));
                // Serializes publishers in separate Unity editors. The OS
                // releases the lock after a crash; the file itself may remain.
                using var mirrorLock = new FileStream(launch + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var marker = Path.Combine(launch, ".complete");
                // Publish a complete fresh directory, never copy through paths
                // in a damaged existing mirror. Preserve old content separately
                // instead of following its links or deleting user-added files.
                var reusable = File.Exists(marker) && File.Exists(Executable(launch)) && VerifyDeclaredHashes(launch, runtime, out _);
                if (!reusable)
                {
                    var staging = launch + ".staging-" + Guid.NewGuid().ToString("N");
                    CopyDirectory(root, staging);
                    ChmodExecutables(staging, runtime);
                    if (!VerifyDeclaredHashes(staging, runtime, out var mirrorError))
                        throw new InvalidOperationException("工作台运行组件镜像未通过完整性校验：" + mirrorError + "。诊断目录：" + staging);
                    File.WriteAllText(Path.Combine(staging, ".complete"), DateTime.UtcNow.ToString("o") + Environment.NewLine);
                    if (Directory.Exists(launch)) Directory.Move(launch, launch + ".previous-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(staging, launch);
                }
            }
            else
            {
                ChmodExecutables(root, runtime);
            }
            if (!HasExecuteBit(Executable(launch)))
                throw new InvalidOperationException("无法赋予工作台程序执行权限：" + Executable(launch) + "。请检查系统安全设置后重试。");
            return launch;
        }

        private static void ChmodExecutables(string root, JObject runtime)
        {
            var names = runtime?["executables"]?.Values<string>().Where(n => !string.IsNullOrEmpty(n)).ToArray()
                ?? new[] { Path.GetFileName(Executable(root)) };
            foreach (var name in names)
            {
                if (!TryResolveBelow(root, name, out var file))
                    throw new InvalidOperationException("运行组件清单声明了越界的可执行文件路径：" + name);
                if (!File.Exists(file) || HasExecuteBit(file)) continue;
                var chmod = ProcessStartInfoShell("/bin/chmod", "755 " + ShellQuote(file));
                using var p = Process.Start(chmod);
                if (p == null) continue;
                if (!p.WaitForExit(15000))
                {
                    // Never leak a timed-out helper: kill it and reap it.
                    try { p.Kill(); } catch (Exception) { }
                    try { p.WaitForExit(5000); } catch (Exception) { }
                }
            }
        }

        private static bool HasExecuteBit(string path)
        {
            try
            {
                using var p = Process.Start(ProcessStartInfoShell("/bin/sh", "-c " + ShellQuote("test -x " + ShellQuote(path))));
                if (p == null) return true; // cannot probe; let the launch itself report
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    try { p.WaitForExit(5000); } catch (Exception) { }
                    return true; // probe inconclusive; let the launch itself report
                }
                return p.ExitCode == 0;
            }
            catch (Exception) { return true; }
        }

        private static ProcessStartInfo ProcessStartInfoShell(string file, string args) =>
            new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };

        private static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, target));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, target), true);
        }

        private static async Task<bool> Healthy(CancellationToken ct)
        {
            try { var health = await DirectorWorkbenchClient.Call("health", null, ct); return (string)health["product"] == "GameDirector" && (int?)health["protocolVersion"] == RequiredProtocol; }
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
                if ((string)health["product"] == "GameDirector" && (int?)health["protocolVersion"] == RequiredProtocol && (string)health["instanceId"] == (string)d["instanceId"]) return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            DirectorWorkbenchClient.Address = previous; return false;
        }
        private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
