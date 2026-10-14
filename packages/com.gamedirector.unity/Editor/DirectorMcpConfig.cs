using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace GameDirector.Unity.Editor
{
    public static class DirectorMcpConfig
    {
        /// <summary>Pure JSON builder: paths are JSON strings, never shell-quoted.
        /// Unity is the default bridge endpoint, so no environment is required.</summary>
        public static string Build(string absoluteGdPath)
        {
            if (string.IsNullOrWhiteSpace(absoluteGdPath))
                throw new ArgumentException(DirectorUi.T("configPathRequired"), nameof(absoluteGdPath));
            return new JObject {
                ["mcpServers"] = new JObject {
                    ["gamedirector"] = new JObject {
                        ["command"] = absoluteGdPath,
                        ["args"] = new JArray("mcp")
                    }
                }
            }.ToString(Formatting.Indented);
        }

        internal static string CopyResolvedConfig(string gd)
        {
            EditorGUIUtility.systemCopyBuffer = Build(gd);
            return DirectorUi.T("configCopied", gd);
        }

        [MenuItem("Window/GameDirector/Copy MCP Config")]
        [MenuItem("Tools/GameDirector/Copy MCP Config")]
        public static void Copy()
        {
            try
            {
                var gd = DirectorWorkbenchLauncher.ResolveBundledCli();
                EditorUtility.DisplayDialog(DirectorUi.T("configTitle"), CopyResolvedConfig(gd), DirectorUi.T("ok"));
            }
            catch (Exception)
            {
                // Keep a single actionable dialog; framework details remain in the exception.
                EditorUtility.DisplayDialog(DirectorUi.T("configTitle"), DirectorUi.T("configFailed", DirectorWorkbenchLauncher.CurrentRid()), DirectorUi.T("ok"));
            }
        }
    }
}
