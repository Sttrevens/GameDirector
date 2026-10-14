using System;
using System.Linq;
using GameDirector.Unity.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace GameDirector.Unity.Tests
{
    public class CopyMcpConfigTests
    {
        private bool hadLanguage;
        private string previousLanguage;
        [SetUp] public void UseEnglishForEnglishDiagnosticAssertions()
        {
            hadLanguage = UnityEditor.EditorPrefs.HasKey(DirectorUi.PreferenceKey);
            previousLanguage = UnityEditor.EditorPrefs.GetString(DirectorUi.PreferenceKey, "");
            UnityEditor.EditorPrefs.SetString(DirectorUi.PreferenceKey, "en");
        }
        [TearDown] public void RestoreLanguage()
        {
            if (hadLanguage) UnityEditor.EditorPrefs.SetString(DirectorUi.PreferenceKey, previousLanguage);
            else UnityEditor.EditorPrefs.DeleteKey(DirectorUi.PreferenceKey);
        }

        [TestCase("/Applications/Game Director/bin/gd")]
        [TestCase("/opt/Studio \"Quoted\"/GameDirector/bin/gd")]
        [TestCase(@"C:\Program Files\GameDirector\Tools~\bin\gd.exe")]
        public void ConfigRoundTripsAbsoluteCommandWithoutEnv(string command)
        {
            var root = JObject.Parse(DirectorMcpConfig.Build(command));
            var servers = root["mcpServers"] as JObject;
            Assert.IsNotNull(servers, "MCP server map missing");
            CollectionAssert.AreEqual(new[] { "mcpServers" }, root.Properties().Select(p => p.Name));
            CollectionAssert.AreEqual(new[] { "gamedirector" }, servers.Properties().Select(p => p.Name));
            var server = (JObject)servers["gamedirector"];
            Assert.AreEqual(command, (string)server["command"]);
            CollectionAssert.AreEqual(new[] { "mcp" }, server["args"].Values<string>());
            CollectionAssert.AreEquivalent(new[] { "command", "args" }, server.Properties().Select(p => p.Name));
            Assert.IsNull(server["env"]);
        }

        [Test] public void ClipboardActionExplainsJsonAgentsCommandAndRecopyAfterUpdate()
        {
            var method = typeof(DirectorMcpConfig).GetMethod("CopyResolvedConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "Clipboard action has no testable result");
            var old = UnityEditor.EditorGUIUtility.systemCopyBuffer;
            try
            {
                const string path = "/Applications/Game Director/bin/gd";
                var message = (string)method.Invoke(null, new object[] { path });
                Assert.AreEqual(path, (string)JObject.Parse(UnityEditor.EditorGUIUtility.systemCopyBuffer)["mcpServers"]["gamedirector"]["command"]);
                Assert.AreEqual("Copied the GameDirector MCP server config (JSON) to the clipboard. Paste it into your agent's MCP settings (Claude Code, Cursor, Codex). It runs: " + path + ". If you update the package, copy it again.", message);
            }
            finally { UnityEditor.EditorGUIUtility.systemCopyBuffer = old; }
        }

        [Test] public void BrokenRuntimeForCopyConfigHasEnglishFailure()
        {
            var method = typeof(DirectorWorkbenchLauncher).GetMethod("ResolveBundledCliFrom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "Bundled CLI error boundary is missing");
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gd-broken-copy-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "gamedirector.runtime.json"), "{");
                var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { root }));
                StringAssert.Contains("bundled GameDirector runtime", error.InnerException.Message);
                Assert.IsFalse(error.InnerException.Message.Any(c => c >= '\u4e00' && c <= '\u9fff'), error.InnerException.Message);
            }
            finally { System.IO.Directory.Delete(root, true); }
        }

        [TestCase(null)] [TestCase("")]
        public void MissingCommandCannotProduceAnUnusableConfig(string command)
        {
            Assert.Throws<ArgumentException>(() => DirectorMcpConfig.Build(command));
        }
    }
}
