using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    public sealed class DirectorWorkbenchWindow : EditorWindow
    {
        private string studio = GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint, endpoint = GameDirector.Core.Dsl.BridgeDefaults.UnityEndpoint, folder = "Assets", search = "", brief = "";
        private string status = "";
        private AssetInventory inventory = new AssetInventory();
        private Queue<string> pending;
        private Vector2 scroll;
        private bool busy, inventoryReady;
        private CancellationTokenSource lifetime;
        private string jobs = "", lastStage;
        private string ProjectId => DirectorWorkbenchClient.ProjectId;

        [MenuItem("Tools/GameDirector/Director Workbench")]
        [MenuItem("Window/GameDirector/Director Workbench")]
        public static void Open() { GetWindow<DirectorWorkbenchWindow>(DirectorUi.T("product")).minSize = new Vector2(350, 500); }
        private void OnEnable() { if (status == "") status = DirectorUi.T("windowStart"); studio = DirectorWorkbenchClient.Address; endpoint = DirectorWorkbenchClient.BridgeAddress; lifetime = new CancellationTokenSource(); EditorApplication.update += Tick; }
        private void OnDisable() { EditorApplication.update -= Tick; pending = null; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
        private void Tick()
        {
            if (pending == null || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var until = EditorApplication.timeSinceStartup + .012;
            try
            {
                while (pending.Count > 0 && EditorApplication.timeSinceStartup < until) inventory.assets.AddRange(DirectorAssetIndex.Read(pending.Dequeue()));
                status = DirectorUi.T("discoveryProgress", pending.Count, inventory.assets.Count);
                if (pending.Count == 0) { pending = null; inventoryReady = true; status = DirectorUi.T("discoveryComplete"); }
            }
            catch (Exception ex) { pending = null; status = ex.Message; }
            Repaint();
        }
        private async void Run(Func<Task> work)
        {
            busy = true;
            try { await work(); } catch (OperationCanceledException) { } catch (Exception ex) { status = ex.Message; }
            finally { busy = false; if (this != null) Repaint(); }
        }
        private Task Sync() => DirectorWorkbenchClient.Register(lifetime.Token, inventoryReady ? inventory : null);
        private void OnGUI()
        {
            GUILayout.Space(12); GUILayout.Label(DirectorUi.T("product"), EditorStyles.boldLabel);
            if (GUILayout.Button(DirectorUi.T("languageToggle"))) DirectorUi.Toggle();
            GUILayout.Label(DirectorUi.T("workflow"), EditorStyles.miniLabel);
            if (GUILayout.Button(DirectorUi.T("startWorkbench"))) Run(async () => { await DirectorWorkbenchLauncher.EnsureStarted(lifetime.Token); studio = DirectorWorkbenchClient.Address; await Sync(); status = DirectorUi.T("windowReady"); });
            if (GUILayout.Button(DirectorUi.T("createExample"))) { try { DirectorExample.Create(); } catch (Exception ex) { status = ex.Message; } }
            if (Selection.activeObject is DirectorFilmAsset selectedFilm && GUILayout.Button(DirectorUi.T("openSelected"))) Run(() => DirectorWorkbenchClient.OpenBrowser(selectedFilm, lifetime.Token));
            EditorGUI.BeginChangeCheck();
            studio = EditorGUILayout.TextField(DirectorUi.T("workbench"), studio); endpoint = EditorGUILayout.TextField(DirectorUi.T("bridge"), endpoint);
            if (EditorGUI.EndChangeCheck()) { try { DirectorWorkbenchClient.Address = studio; DirectorWorkbenchClient.BridgeAddress = endpoint; } catch (Exception ex) { status = ex.Message; } }
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button(DirectorUi.T("syncAssets"))) Run(async () => { await Sync(); status = DirectorUi.T("assetsSynced"); });
                if (GUILayout.Button(DirectorUi.T("refreshRoles"))) Run(async () => { await Sync(); await DirectorWorkbenchClient.Call("projects/" + ProjectId + "/connect", new { }, lifetime.Token); var state = (JObject)await DirectorWorkbenchClient.Call("state", null, lifetime.Token); jobs = string.Join("\n", state["jobs"].Where(j => (string)j["request"]["projectId"] == ProjectId).Select(j => (string)j["request"]["film"]["title"] + " · " + DirectorUi.State((string)j["state"]) + "\n" + (string)j["progress"])); status = DirectorUi.T("rolesRefreshed"); });
            }
            if (GUILayout.Button(DirectorUi.T("fullWindow"))) Run(() => DirectorWorkbenchClient.OpenBrowser(null, lifetime.Token));
            EditorGUILayout.Space(); folder = EditorGUILayout.TextField(DirectorUi.T("assetFolder"), folder);
            using (new EditorGUI.DisabledScope(pending != null || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(DirectorUi.T("discoverAssets"))) { try { inventoryReady = false; inventory = new AssetInventory(); pending = new Queue<string>(DirectorAssetIndex.Paths(folder)); } catch (Exception ex) { status = ex.Message; } }
            if (pending != null && GUILayout.Button(DirectorUi.T("stopDiscovery"))) pending = null;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || !(Selection.activeObject is GameObject) || !EditorUtility.IsPersistent(Selection.activeObject)))
                if (GUILayout.Button(DirectorUi.T("createStage"))) { try { lastStage = DirectorStageFactory.Create((GameObject)Selection.activeObject); status = DirectorUi.T("createdStage", lastStage); } catch (Exception ex) { status = ex.Message; } }
            if (!string.IsNullOrEmpty(lastStage) && GUILayout.Button(DirectorUi.T("locateCreatedStage"))) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(lastStage));
            EditorGUILayout.HelpBox(status, MessageType.Info);
            search = EditorGUILayout.TextField(DirectorUi.T("findAsset"), search);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(90), GUILayout.MaxHeight(220));
            foreach (var entry in inventory.assets.Where(e => (e.name + e.id).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).Take(100))
            {
                if (GUILayout.Button(entry.name + " · " + entry.kind, EditorStyles.miniButton)) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(entry.path));
                EditorGUILayout.SelectableLabel(entry.id, EditorStyles.miniLabel, GUILayout.Height(16));
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField(DirectorUi.T("directApi"), EditorStyles.boldLabel);
            brief = EditorGUILayout.TextArea(brief, GUILayout.MinHeight(65));
            EditorGUILayout.HelpBox(DirectorUi.T("apiNote"), MessageType.None);
            using (new EditorGUI.DisabledScope(busy || string.IsNullOrWhiteSpace(brief)))
                if (GUILayout.Button(DirectorUi.T("directCut"))) Run(async () => {
                    await Sync();
                    var path = "projects/" + ProjectId + "/documents/default";
                    var current = await DirectorWorkbenchClient.Call(path, null, lifetime.Token);
                    var reply = await DirectorWorkbenchClient.Operation("direct", new JObject { ["projectId"] = ProjectId, ["brief"] = brief, ["currentFilm"] = current["film"], ["render"] = false }, lifetime.Token);
                    var saved = await DirectorWorkbenchClient.Operation(path, new JObject { ["expectedRevision"] = current["revision"], ["film"] = reply["film"] }, lifetime.Token);
                    var job = await DirectorWorkbenchClient.Operation(path + "/produce", new JObject { ["revision"] = saved["revision"], ["preview"] = false }, lifetime.Token);
                    status = DirectorUi.T("productionQueued", (string)job["id"]);
                });
            if (!string.IsNullOrEmpty(jobs)) EditorGUILayout.HelpBox(jobs, MessageType.None);
        }
    }
}
