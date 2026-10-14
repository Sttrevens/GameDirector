using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using GameDirector.Unity.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GameDirector.Unity.Tests
{
    public class SharedRuntimeIntegrityTests
    {
        private string root;
        private JObject manifest;
        private string Cli => Application.platform == RuntimePlatform.WindowsEditor ? "gd.exe" : "gd";

        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "gd-unity-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var ext = Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : "";
            var apps = new JObject { ["cli"] = Cli, ["mcp"] = "gamedirector-mcp" + ext, ["workbench"] = "gamedirector-workbench" + ext };
            var hashes = new JObject();
            foreach (var name in new[] { Cli, "gamedirector-mcp" + ext, "gamedirector-workbench" + ext, "shared.dll" })
            {
                var path = Path.Combine(root, name); File.WriteAllText(path, name);
                using var sha = SHA256.Create();
                hashes[name] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            }
            manifest = new JObject { ["product"] = "GameDirector", ["app"] = "shared", ["apps"] = apps,
                ["executables"] = new JArray(Cli, "gamedirector-mcp" + ext, "gamedirector-workbench" + ext), ["sha256"] = hashes };
            Save();
        }

        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static object Call(string method, params object[] args) =>
            typeof(DirectorWorkbenchLauncher).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
        private void SetMode(string path, string mode)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/chmod", mode + " \"" + path + "\"") { UseShellExecute = false });
            p.WaitForExit(); Assert.AreEqual(0, p.ExitCode);
        }
        private void Save() => File.WriteAllText(Path.Combine(root, "gamedirector.runtime.json"), manifest.ToString());
        private bool Verify(out string error)
        {
            var method = typeof(DirectorWorkbenchLauncher).GetMethod("VerifyDeclaredHashes", BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { root, manifest, null };
            var ok = (bool)method.Invoke(null, args); error = (string)args[2]; return ok;
        }

        [Test] public void CompleteSharedPayloadPasses() { Assert.IsTrue(Verify(out var error), error); }

        [TestCase("cli")] [TestCase("mcp")] [TestCase("workbench")] [TestCase("shared.dll")]
        public void TamperingAnyAppOrSharedAssemblyFails(string app)
        {
            var name = (string)manifest["apps"][app] ?? app;
            File.WriteAllText(Path.Combine(root, name), "modified");
            Assert.IsFalse(Verify(out var error)); StringAssert.Contains(name, error);
        }

        [Test] public void EveryDeclaredAppMustHaveAHash()
        {
            ((JObject)manifest["sha256"]).Remove(Cli); Save();
            Assert.IsFalse(Verify(out var error)); StringAssert.Contains(Cli, error);
        }

        [TestCase(".DS_Store")] [TestCase("._gd")] [TestCase("Thumbs.db")]
        [TestCase("desktop.ini")] [TestCase("Icon\r")]
        public void OsMetadataAtRootAndInSubfoldersIsIgnored(string name)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Assert.IsTrue(GameDirector.Core.Dsl.RuntimeFilePolicy.IsOsMetadata(name));
                return; // Finder Icon<CR> cannot exist on Windows.
            }
            var nested = Path.Combine(root, "Fonts"); Directory.CreateDirectory(nested);
            foreach (var folder in new[] { root, nested }) File.WriteAllText(Path.Combine(folder, name), "OS metadata");
            Assert.IsTrue(Verify(out var error), error);
        }

        [Test] public void MetadataLookalikesRemainFatal()
        {
            File.WriteAllText(Path.Combine(root, ".DS_Store.dll"), "payload");
            Assert.IsFalse(Verify(out var error)); StringAssert.Contains(".DS_Store.dll", error);
        }

        [Test] public void UnlistedRuntimePayloadFails()
        {
            File.WriteAllText(Path.Combine(root, "injected.dll"), "not declared");
            Assert.IsFalse(Verify(out var error)); StringAssert.Contains("injected.dll", error);
        }

        [TestCase("old-layout")] [TestCase("broken-json")] [TestCase("missing-folder")]
        public void InvalidRuntimePreferenceIsIgnoredAndCanBeCleared(string damage)
        {
            var old = DirectorWorkbenchLauncher.RuntimeFolder;
            try
            {
                var selected = damage == "missing-folder" ? root + "-missing" : root;
                if (damage == "old-layout") File.Delete(Path.Combine(root, "gamedirector.runtime.json"));
                if (damage == "broken-json") File.WriteAllText(Path.Combine(root, "gamedirector.runtime.json"), "{");
                DirectorWorkbenchLauncher.RuntimeFolder = selected;
                var method = typeof(DirectorWorkbenchLauncher).GetMethod("TryPreferredRuntime", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(method, "Invalid preference fallback is missing");
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Ignoring invalid GameDirector.RuntimeFolder"));
                var args = new object[] { null, null };
                Assert.IsFalse((bool)method.Invoke(null, args)); Assert.IsNull(args[0]);
                var clear = typeof(DirectorWorkbenchLauncher).GetMethod("ClearRuntimeFolder"); Assert.IsNotNull(clear);
                clear.Invoke(null, null); Assert.AreEqual("", DirectorWorkbenchLauncher.RuntimeFolder);
            }
            finally { DirectorWorkbenchLauncher.RuntimeFolder = old; }
        }

        [Test] public void BrokenMirrorManifestIsRebuiltRatherThanThrown()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) Assert.Ignore("Unix mirror repair");
            var oldHome = Environment.GetEnvironmentVariable("GAMEDIRECTOR_HOME"); var home = root + "-home";
            var files = Directory.GetFiles(root); var cache = Path.Combine(root, "PackageCache", "package", "Tools~", "bin");
            Directory.CreateDirectory(cache);
            foreach (var file in files) { var target = Path.Combine(cache, Path.GetFileName(file)); File.Copy(file, target); SetMode(target, "644"); }
            try
            {
                Environment.SetEnvironmentVariable("GAMEDIRECTOR_HOME", home);
                var mirror = (string)Call("PrepareExecutable", cache, manifest);
                Assert.AreNotEqual(cache, mirror);
                File.WriteAllText(Path.Combine(mirror, "gamedirector.runtime.json"), "{");
                Assert.AreEqual(mirror, Call("PrepareExecutable", cache, manifest));
                Assert.DoesNotThrow(() => JObject.Parse(File.ReadAllText(Path.Combine(mirror, "gamedirector.runtime.json"))));
            }
            finally { Environment.SetEnvironmentVariable("GAMEDIRECTOR_HOME", oldHome); if (Directory.Exists(home)) Directory.Delete(home, true); }
        }

        [Test] public void MissingCliExecuteBitIsRefusedEvenIfOtherProgramsAreExecutable()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) Assert.Ignore("Unix permissions");
            foreach (var name in ((JObject)manifest["apps"]).Properties()) SetMode(Path.Combine(root, (string)name.Value), "755");
            SetMode(Path.Combine(root, Cli), "644");
            manifest["executables"] = new JArray((string)manifest["apps"]["workbench"]); Save();
            var error = Assert.Throws<TargetInvocationException>(() => Call("PrepareExecutable", root, manifest));
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        }

        [Test] public void FullyExecutablePackageCacheRuntimeDoesNotCreateMirrorAndLaunchesCli()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) Assert.Ignore("Unix executable fixture");
            var oldHome = Environment.GetEnvironmentVariable("GAMEDIRECTOR_HOME");
            var home = root + "-home"; var cache = Path.Combine(root, "PackageCache", "package", "Tools~", "bin");
            Directory.CreateDirectory(cache);
            foreach (var name in ((JObject)manifest["apps"]).Properties())
            {
                var path = Path.Combine(cache, (string)name.Value); File.WriteAllText(path, "#!/bin/sh\nprintf 'fixture-cli'\n"); SetMode(path, "755");
            }
            File.WriteAllText(Path.Combine(cache, "gamedirector.runtime.json"), manifest.ToString());
            try
            {
                Environment.SetEnvironmentVariable("GAMEDIRECTOR_HOME", home);
                Assert.AreEqual(cache, Call("PrepareExecutable", cache, manifest));
                Assert.IsFalse(Directory.Exists(home));
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(cache, Cli)) { UseShellExecute = false, RedirectStandardOutput = true });
                Assert.AreEqual("fixture-cli", p.StandardOutput.ReadToEnd()); p.WaitForExit(); Assert.AreEqual(0, p.ExitCode);
            }
            finally { Environment.SetEnvironmentVariable("GAMEDIRECTOR_HOME", oldHome); if (Directory.Exists(home)) Directory.Delete(home, true); }
        }

        [TestCase(0xA000000Cu, true)] [TestCase(0xA0000003u, true)]
        [TestCase(0xA0001027u, true)] [TestCase(0x9000001Au, false)]
        [TestCase(0x9000101Au, false)] [TestCase(0x80000015u, false)] [TestCase(0u, false)]
        public void WindowsTagsRejectOnlyNameSurrogateLinks(uint tag, bool expected)
        {
            var method = typeof(GameDirector.Core.Dsl.RuntimeFilePolicy).GetMethod("IsWindowsLinkTag");
            Assert.IsNotNull(method, "Windows reparse-tag classifier is missing");
            Assert.AreEqual(expected, (bool)method.Invoke(null, new object[] { tag }));
        }

        [Test] public void UnixSymlinkedPayloadIsRefused()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) Assert.Ignore("Unix symlink fixture");
            var payload = Path.Combine(root, "shared.dll"); var outside = root + ".dll";
            File.Move(payload, outside);
            try
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/ln", "-s \"" + outside + "\" \"" + payload + "\"") { UseShellExecute = false }))
                { p.WaitForExit(); Assert.AreEqual(0, p.ExitCode); }
                Assert.IsFalse(Verify(out var error)); StringAssert.Contains("shared.dll", error);
            }
            finally { File.Delete(payload); File.Delete(outside); }
        }

        [Test] public void UnsafeAppPathFails()
        {
            manifest["apps"]["cli"] = "../outside"; Save();
            Assert.IsFalse(Verify(out var error)); StringAssert.Contains("../outside", error);
        }
    }
}
