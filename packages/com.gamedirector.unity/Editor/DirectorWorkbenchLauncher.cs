using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GameDirector.Core.Dsl;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    /// <summary>Launch the workbench runtime shipped inside the full UPM package
    /// (Tools~/bin). Validates platform/protocol compatibility against the
    /// bundled gamedirector.runtime.json, repairs Unix executable permissions
    /// lost by cross-built archives, and never writes into PackageCache — when
    /// the package lives there and needs chmod, the runtime is mirrored into the
    /// per-user data folder first. A source-only (Git UPM) install carries no
    /// runtime and gets a clear, actionable message.</summary>
    public static class DirectorWorkbenchLauncher
    {
        private static readonly SemaphoreSlim starting = new SemaphoreSlim(1, 1);
        private const string RuntimeFolderPreference = "GameDirector.RuntimeFolder";
        public static string RuntimeFolder
        {
            get => EditorPrefs.GetString(RuntimeFolderPreference, "");
            set => EditorPrefs.SetString(RuntimeFolderPreference, value);
        }
        [MenuItem("Tools/GameDirector/Clear Runtime Folder Preference")]
        public static void ClearRuntimeFolder() => EditorPrefs.DeleteKey(RuntimeFolderPreference);

        private const string RuntimeManifestFile = "gamedirector.runtime.json";
        private static string Executable(string root) => AppExecutable(root, "workbench");

        private static string AppExecutable(string root, string app)
        {
            root = Path.GetFullPath(string.IsNullOrEmpty(root) ? "." : root);
            var path = Path.Combine(root, RuntimeManifestFile);
            var name = File.Exists(path) ? (string)JObject.Parse(File.ReadAllText(path))["apps"]?[app] : null;
            if (name == null && app == "workbench" && !File.Exists(path))
                name = Application.platform == RuntimePlatform.WindowsEditor ? "gamedirector-workbench.exe" : "gamedirector-workbench";
            if (!TryResolveBelow(root, name, out var full))
                throw new InvalidOperationException(DirectorUi.T("unsafeProgram") + app);
            return full;
        }

        /// <summary>Read the generated copy of the canonical distribution layout.</summary>
        public static string BundledRuntimeRoot(string packageRoot)
        {
            var path = Path.Combine(packageRoot, "distribution.json");
            if (!File.Exists(path)) return null; // source-only package
            var layout = JObject.Parse(File.ReadAllText(path));
            var relative = (string)layout["unity"]?["bundledRuntimeParent"] + "/" + (string)layout["runtimeFolder"];
            if (!TryResolveBelow(packageRoot, relative, out var full))
                throw new InvalidOperationException(DirectorUi.T("unsafeRuntime"));
            return full;
        }

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

        // Product, protocol and the per-user data root are stated once in Core
        // (WorkbenchService/GameDirectorPaths) so the launcher and the Workbench
        // can never drift onto two different stores or contracts.
        private static bool IsPackageCache(string path) => path.Replace('\\', '/').Contains("/PackageCache/");

        /// <summary>Validate the bundled runtime's manifest against this editor.
        /// Returns the manifest when compatible; throws with an actionable
        /// message otherwise. A null manifest path means "no manifest shipped":
        /// only acceptable for a user-chosen legacy runtime folder.</summary>
        private static JObject ValidateRuntimeManifest(string root, bool fromPackage)
        {
            var manifestPath = Path.Combine(root, RuntimeManifestFile);
            if (!File.Exists(manifestPath))
            {
                if (fromPackage)
                    throw new InvalidOperationException(
                        DirectorUi.T("missingManifest", CurrentRid()));
                return null;
            }
            JObject manifest;
            try { manifest = JObject.Parse(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { throw new InvalidOperationException(DirectorUi.T("parseManifest") + manifestPath + " — " + ex.Message); }
            var rid = (string)manifest["rid"];
            var protocol = (int?)manifest["protocolVersion"] ?? 0;
            if ((string)manifest["product"] != WorkbenchService.Product)
                throw new InvalidOperationException(DirectorUi.T("wrongProduct") + manifestPath);
            if (rid != CurrentRid())
                throw new InvalidOperationException(
                    DirectorUi.T("wrongRid", rid, CurrentRid()));
            if (protocol != WorkbenchService.ProtocolVersion)
                throw new InvalidOperationException(
                    DirectorUi.T("wrongProtocol", protocol, WorkbenchService.ProtocolVersion));
            if ((string)manifest["app"] != "shared" || !(manifest["apps"] is JObject))
                throw new InvalidOperationException(DirectorUi.T("sharedRuntimeRequired"));
            return manifest;
        }

        /// <summary>Declared-path safety: every sha256 key and executable name in
        /// the runtime manifest must resolve to a path below the runtime folder.</summary>
        private static bool TryResolveBelow(string root, string relative, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(":")) return false;
            foreach (var segment in relative.Replace('\\', '/').Split('/'))
                if (segment == ".." || segment == "." || segment.Length == 0) return false;
            full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        /// <summary>Hash-verify every file the runtime manifest declares, and confirm
        /// declared executable paths stay below the runtime folder. A manifest
        /// must include its entry executable in the hash map.</summary>
        private static bool VerifyDeclaredHashes(string root, JObject runtime, out string error)
        {
            if (runtime == null) { error = null; return true; }
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var map = runtime["sha256"] as JObject;
            if (map == null || map[Path.GetFileName(Executable(root))] == null)
            { error = DirectorUi.T("missingHashes"); return false; }
            var apps = runtime["apps"] as JObject;
            if (apps == null) { error = DirectorUi.T("missingApps"); return false; }
            var declared = apps.Properties().Select(p => (string)p.Value)
                .Concat((runtime["executables"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
            foreach (var name in declared)
            {
                if (!TryResolveBelow(root, name, out _)) { error = DirectorUi.T("unsafeApp") + name; return false; }
                if (map[name] == null) { error = DirectorUi.T("missingAppHash") + name; return false; }
            }
            if (map != null)
            {
                foreach (var prop in map.Properties())
                {
                    if (!TryResolveBelow(root, prop.Name, out var full)) { error = DirectorUi.T("unsafePath") + prop.Name; return false; }
                    if (!File.Exists(full)) { error = DirectorUi.T("missingFile") + prop.Name; return false; }
                    for (var path = full; ; path = Path.GetDirectoryName(path))
                    {
                        if (RuntimeLinks.IsLink(path))
                        { error = DirectorUi.T("linkedPath") + prop.Name; return false; }
                        if (path == Path.GetFullPath(root)) break;
                    }
                    string actual;
                    try
                    {
                        using (var stream = File.OpenRead(full))
                        using (var sha = SHA256.Create())
                            actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    }
                    catch (IOException ex) { error = DirectorUi.T("unreadableFile", prop.Name, ex.Message); return false; }
                    catch (UnauthorizedAccessException ex) { error = DirectorUi.T("unreadableFile", prop.Name, ex.Message); return false; }
                    if (!string.Equals(actual, (string)prop.Value, StringComparison.OrdinalIgnoreCase)) { error = DirectorUi.T("changedFile") + prop.Name; return false; }
                }
            }
            var executables = runtime["executables"] as JArray;
            if (executables != null)
                foreach (var name in executables.Values<string>())
                    if (!TryResolveBelow(root, name, out _)) { error = DirectorUi.T("unsafeExecutable") + name; return false; }
            var pending = new System.Collections.Generic.Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
                foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var relative = path.Substring(Path.GetFullPath(root).Length + 1).Replace('\\', '/');
                    var attributes = File.GetAttributes(path);
                    if (RuntimeLinks.IsLink(path)) { error = DirectorUi.T("linkedPath") + relative; return false; }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                    // .complete is the existing mirror's non-executable publication marker.
                    if (relative != RuntimeManifestFile && relative != GameDirectorPaths.RuntimeMirrorMarkerFile &&
                        !RuntimeFilePolicy.IsOsMetadata(Path.GetFileName(path)) && map[relative] == null)
                    { error = DirectorUi.T("undeclaredFile") + relative; return false; }
                }
            error = null;
            return true;
        }

        /// <summary>Fail honestly when a manifest-carrying runtime was modified or
        /// corrupted; legacy folders without a manifest keep their previous behavior.</summary>
        private static void VerifyRuntimeIntegrity(string root, JObject runtime)
        {
            if (runtime == null) return;
            if (!VerifyDeclaredHashes(root, runtime, out var error))
                throw new InvalidOperationException(DirectorUi.T("integrityFailed", error));
        }

        /// <summary>Resolve bundled gd using the Workbench's SAME compatibility,
        /// whole-tree integrity and Unix permission/mirror gates. No new trust path.</summary>
        public static string ResolveBundledCli()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DirectorWorkbenchLauncher).Assembly);
            var bundled = package != null ? BundledRuntimeRoot(package.resolvedPath) : null;
            if (bundled == null)
                throw new InvalidOperationException(DirectorUi.T("noBundledCli", CurrentRid()));
            return ResolveBundledCliFrom(bundled);
        }

        private static string ResolveBundledCliFrom(string bundled)
        {
            try
            {
                var runtime = ValidateRuntimeManifest(bundled, fromPackage: true);
                VerifyRuntimeIntegrity(bundled, runtime);
                var root = PrepareExecutable(bundled, runtime);
                var cli = AppExecutable(root, "cli");
                RequireExecuteBit(cli);
                return cli;
            }
            catch (Exception ex)
            {
                // Copy MCP Config keeps one actionable failure boundary.
                throw new InvalidOperationException(DirectorUi.T("bundledCliFailed", bundled, CurrentRid()), ex);
            }
        }

        private static bool TryPreferredRuntime(out string root, out JObject runtime)
        {
            root = null; runtime = null;
            var selected = RuntimeFolder;
            if (string.IsNullOrWhiteSpace(selected)) return false;
            try
            {
                var full = Path.GetFullPath(selected);
                // Old per-app layouts are not a shared runtime preference.
                var manifest = ValidateRuntimeManifest(full, fromPackage: true);
                VerifyRuntimeIntegrity(full, manifest);
                if (!File.Exists(Executable(full))) throw new InvalidOperationException(DirectorUi.T("missingWorkbench"));
                root = full; runtime = manifest;
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(DirectorUi.T("invalidPreference", RuntimeFolderPreference, selected, ex.Message));
                return false;
            }
        }

        public static async Task EnsureStarted(CancellationToken ct = default)
        {
            await starting.WaitAsync(ct);
            try
            {
                if (await Healthy(ct)) return;
                if (Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH") != null)
                    throw new InvalidOperationException(DirectorUi.T("unauthenticated"));
                var data = GameDirectorPaths.WorkbenchStore();
                if (TryPreferredRuntime(out var preferred, out var preferredRuntime))
                {
                    await StartFrom(PrepareExecutable(preferred, preferredRuntime), data, ct);
                    return;
                }
                var root = "";
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DirectorWorkbenchLauncher).Assembly);
                    if (package != null)
                    {
                        var bundled = BundledRuntimeRoot(package.resolvedPath);
                        if (bundled != null && File.Exists(Path.Combine(bundled, RuntimeManifestFile)))
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
                        EditorUtility.DisplayDialog(DirectorUi.T("workbenchTitle"),
                            DirectorUi.T("sourceOnly", CurrentRid()),
                            DirectorUi.T("understood"));
                    }
                }
                var picked = EditorUtility.OpenFolderPanel(DirectorUi.T("runtimeFolderTitle"), RuntimeFolder, "");
                if (picked == "") throw new OperationCanceledException();
                if (!File.Exists(Executable(picked))) throw new InvalidOperationException(DirectorUi.T("wrongRuntimeFolder"));
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
            // MediaTools.Resolve already probes runtime/media, ../media and
            // GameDirectorPaths.Media. Keep explicit user env overrides intact.
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
                throw new InvalidOperationException(DirectorUi.T("startFailed", Executable(root), ex.Message, logPath), ex);
            }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            // The service owns its lifetime; closing an Inspector does not stop jobs.
            for (var i = 0; i < 60; i++)
            {
                if (await Discover(Path.Combine(data, GameDirectorPaths.ServiceDescriptorFile), ct)) return;
                if (process.HasExited) throw new InvalidOperationException(DirectorUi.T("launchExited", logPath));
                await Task.Delay(250, ct);
            }
            throw new InvalidOperationException(DirectorUi.T("launchTimeout", logPath));
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
            var names = runtime?["executables"]?.Values<string>().ToArray() ?? new[] { Path.GetFileName(exe) };
            if (names.All(name => TryResolveBelow(root, name, out var file) && HasExecuteBit(file)))
            {
                RequireRuntimeEntryBits(root, runtime);
                return root;
            }
            var launch = root;
            if (IsPackageCache(root))
            {
                // Content-addressed mirror in the per-user data folder; PackageCache
                // itself stays untouched.
                var key = "runtime";
                try
                {
                    var manifestPath = Path.Combine(root, RuntimeManifestFile);
                    if (File.Exists(manifestPath))
                    {
                        using var sha = SHA256.Create();
                        key = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(manifestPath))).Replace("-", "").ToLowerInvariant();
                    }
                }
                catch (IOException) { }
                launch = Path.Combine(GameDirectorPaths.RuntimeMirror(), key);
                Directory.CreateDirectory(Path.GetDirectoryName(launch));
                // Serializes publishers in separate Unity editors. The OS
                // releases the lock after a crash; the file itself may remain.
                using var mirrorLock = new FileStream(launch + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // Publish a complete fresh directory, never copy through paths
                // in a damaged existing mirror. Preserve old content separately
                // instead of following its links or deleting user-added files.
                var reusable = IsReusableMirror(launch, runtime);
                if (!reusable)
                {
                    var staging = launch + ".staging-" + Guid.NewGuid().ToString("N");
                    CopyDirectory(root, staging);
                    ChmodExecutables(staging, runtime);
                    if (!VerifyDeclaredHashes(staging, runtime, out var mirrorError))
                        throw new InvalidOperationException(DirectorUi.T("mirrorIntegrity", mirrorError, staging));
                    File.WriteAllText(Path.Combine(staging, GameDirectorPaths.RuntimeMirrorMarkerFile), DateTime.UtcNow.ToString("o") + Environment.NewLine);
                    if (Directory.Exists(launch)) Directory.Move(launch, launch + ".previous-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(staging, launch);
                }
            }
            else
            {
                ChmodExecutables(root, runtime);
            }
            RequireRuntimeEntryBits(launch, runtime);
            return launch;
        }

        private static bool IsReusableMirror(string root, JObject runtime)
        {
            try
            {
                var path = Path.Combine(root, RuntimeManifestFile);
                return File.Exists(Path.Combine(root, GameDirectorPaths.RuntimeMirrorMarkerFile)) && File.Exists(path) &&
                    JToken.DeepEquals(JObject.Parse(File.ReadAllText(path)), runtime) && File.Exists(Executable(root)) &&
                    VerifyDeclaredHashes(root, runtime, out _);
            }
            catch (Exception) { return false; } // A damaged mirror is cache, not authoritative input.
        }

        private static void ChmodExecutables(string root, JObject runtime)
        {
            var names = runtime?["executables"]?.Values<string>().Where(n => !string.IsNullOrEmpty(n)).ToArray()
                ?? new[] { Path.GetFileName(Executable(root)) };
            var files = names.Select(name => {
                if (!TryResolveBelow(root, name, out var file) || !File.Exists(file))
                    throw new InvalidOperationException(DirectorUi.T("unsafeRuntimeExecutable") + name);
                return file;
            }).ToArray();
            if (files.Length == 0) return;
            // One batched helper, not a shell/chmod pair per file on the editor thread.
            using var p = Process.Start(ProcessStartInfoShell("/bin/chmod", "755 " + string.Join(" ", files.Select(Quote))));
            if (p == null) throw new InvalidOperationException(DirectorUi.T("repairStart"));
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch (Exception) { }
                try { p.WaitForExit(5000); } catch (Exception) { }
                throw new InvalidOperationException(DirectorUi.T("repairTimeout"));
            }
            if (p.ExitCode != 0) throw new InvalidOperationException(DirectorUi.T("repairFailed") + p.StandardError.ReadToEnd());
        }

        private static void RequireRuntimeEntryBits(string root, JObject runtime)
        {
            RequireExecuteBit(Executable(root));
            if (runtime?["apps"]?["cli"] != null) RequireExecuteBit(AppExecutable(root, "cli"));
        }

        private static void RequireExecuteBit(string path)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && !HasExecuteBit(path))
                throw new InvalidOperationException(DirectorUi.T("notExecutable") + path);
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "access", SetLastError = true)]
        private static extern int UnixAccess(string path, int mode);

        private static bool HasExecuteBit(string path) => File.Exists(path) && UnixAccess(path, 1) == 0;

        private static ProcessStartInfo ProcessStartInfoShell(string file, string args) =>
            new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };

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
            if (Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH") == null) return await Discover(GameDirectorPaths.ServiceDescriptor(), ct);
            try {
                var health = await DirectorWorkbenchClient.Call("health", null, ct);
                if ((string)health["product"] != WorkbenchService.Product || (int?)health["protocolVersion"] != WorkbenchService.ProtocolVersion) return false;
                await DirectorWorkbenchClient.Call("state", null, ct); return true; // explicit env credential must authenticate too
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return false; }
        }
        internal static async Task<bool> Discover(string descriptor, CancellationToken ct)
        {
            if (!File.Exists(descriptor)) return false;
            var previous = DirectorWorkbenchClient.Address;
            try
            {
                var d = JObject.Parse(File.ReadAllText(descriptor)); DirectorWorkbenchClient.Address = (string)d["address"];
                var health = await DirectorWorkbenchClient.Call("health", null, ct);
                if ((string)health["product"] == WorkbenchService.Product && (int?)health["protocolVersion"] == WorkbenchService.ProtocolVersion &&
                    !string.IsNullOrEmpty((string)d["instanceId"]) && (string)health["instanceId"] == (string)d["instanceId"] && !string.IsNullOrEmpty((string)d["token"])) {
                    DirectorWorkbenchClient.UseDescriptor((string)d["address"], (string)d["token"]); return true;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            DirectorWorkbenchClient.Address = previous; return false;
        }
        private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
