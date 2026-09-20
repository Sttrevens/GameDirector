using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    public sealed class DirectorWorkbenchWindow : EditorWindow
    {
        private string studio = GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint, endpoint = GameDirector.Core.Dsl.BridgeDefaults.UnityEndpoint, folder = "Assets", search = "", brief = "", status = "Connect the local Workbench to begin.";
        private AssetInventory inventory = new AssetInventory();
        private Queue<string> pending;
        private Vector2 scroll;
        private bool busy, inventoryReady;
        private CancellationTokenSource lifetime;
        private string jobs = "", lastStage;
        private string ProjectId => "unity-" + Hash128.Compute(Path.GetFullPath(Application.dataPath)).ToString();

        [MenuItem("Tools/GameDirector/Director Workbench")]
        public static void Open() { GetWindow<DirectorWorkbenchWindow>("GameDirector").minSize = new Vector2(350, 500); }
        private void OnEnable() { studio = DirectorWorkbenchClient.Address; endpoint = DirectorWorkbenchClient.BridgeAddress; lifetime = new CancellationTokenSource(); EditorApplication.update += Tick; }
        private void OnDisable() { EditorApplication.update -= Tick; pending = null; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
        private void Tick()
        {
            if (pending == null || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var until = EditorApplication.timeSinceStartup + .012;
            try
            {
                while (pending.Count > 0 && EditorApplication.timeSinceStartup < until) inventory.assets.AddRange(DirectorAssetIndex.Read(pending.Dequeue()));
                status = pending.Count + " asset files remaining · " + inventory.assets.Count + " entries";
                if (pending.Count == 0) { pending = null; inventoryReady = true; status = "Asset discovery complete. Sync to the Workbench; discovered assets still need performance bindings."; }
            }
            catch (Exception ex) { pending = null; status = ex.Message; }
            Repaint();
        }
        private async Task<string> Call(string path, object body = null)
        {
            if (!Uri.TryCreate(studio, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != GameDirector.Core.Dsl.BridgeDefaults.LoopbackHost) throw new ArgumentException("Use a local Workbench address: " + GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint);
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(190) })
            {
                var url = studio.TrimEnd('/') + "/api/" + path;
                using (var response = body == null ? await client.GetAsync(url, lifetime.Token) : await client.PostAsync(url, new StringContent(BridgeJson.Serialize(body), Encoding.UTF8, "application/json"), lifetime.Token))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException(text);
                    return text;
                }
            }
        }
        private async void Run(Func<Task> work)
        {
            busy = true;
            try { await work(); } catch (OperationCanceledException) { } catch (Exception ex) { status = ex.Message; }
            finally { busy = false; if (this != null) Repaint(); }
        }
        private Task<string> Sync() => Call("projects", new { id = ProjectId, name = Application.productName, engine = "Unity", sourceRoot = Path.GetDirectoryName(Application.dataPath), endpoint, inventory = inventoryReady ? inventory : null });
        private void OnGUI()
        {
            GUILayout.Space(12); GUILayout.Label("GameDirector", EditorStyles.boldLabel);
            GUILayout.Label("Assets → Performance → Camera → Film", EditorStyles.miniLabel);
            if (GUILayout.Button("Start / connect Workbench")) Run(async () => { await DirectorWorkbenchLauncher.EnsureStarted(lifetime.Token); studio = DirectorWorkbenchClient.Address; await Sync(); status = "Workbench is ready. Create or select a Director Film asset to edit in the Inspector."; });
            if (GUILayout.Button("Create independent example")) { try { DirectorExample.Create(); } catch (Exception ex) { status = ex.Message; } }
            if (Selection.activeObject is DirectorFilmAsset selectedFilm && GUILayout.Button("Open selected film in browser")) DirectorWorkbenchClient.OpenBrowser(selectedFilm);
            EditorGUI.BeginChangeCheck();
            studio = EditorGUILayout.TextField("Workbench", studio); endpoint = EditorGUILayout.TextField("Play Mode bridge", endpoint);
            if (EditorGUI.EndChangeCheck()) { try { DirectorWorkbenchClient.Address = studio; DirectorWorkbenchClient.BridgeAddress = endpoint; } catch (Exception ex) { status = ex.Message; } }
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Connect / sync discovered assets")) Run(async () => { await Sync(); status = "Assets synced. Start an offline stage in Play Mode to connect its performance bindings."; });
                if (GUILayout.Button("Refresh live roles and production jobs")) Run(async () => { await Sync(); await Call("projects/" + ProjectId + "/connect", new { }); var state = JObject.Parse(await Call("state")); jobs = string.Join("\n", state["jobs"].Where(j => (string)j["request"]["projectId"] == ProjectId).Select(j => (string)j["request"]["film"]["title"] + " · " + (string)j["state"] + "\n" + (string)j["progress"])); status = "Live performance bindings refreshed."; });
            }
            if (GUILayout.Button("Open full directing and review window")) Application.OpenURL("http://" + GameDirector.Core.Dsl.BridgeDefaults.LoopbackHost + ":" + (Uri.TryCreate(studio, UriKind.Absolute, out var local) ? local.Port : GameDirector.Core.Dsl.BridgeDefaults.WorkbenchPort));
            EditorGUILayout.Space(); folder = EditorGUILayout.TextField("Asset folder", folder);
            using (new EditorGUI.DisabledScope(pending != null || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Discover project assets (read only)")) { try { inventoryReady = false; inventory = new AssetInventory(); pending = new Queue<string>(DirectorAssetIndex.Paths(folder)); } catch (Exception ex) { status = ex.Message; } }
            if (pending != null && GUILayout.Button("Stop discovery")) pending = null;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || !(Selection.activeObject is GameObject) || !EditorUtility.IsPersistent(Selection.activeObject)))
                if (GUILayout.Button("Create offline stage from selected asset (writes new scene)")) { try { lastStage = DirectorStageFactory.Create((GameObject)Selection.activeObject); status = "Created " + lastStage + ". Open this scene in isolation and enter Play Mode to shoot."; } catch (Exception ex) { status = ex.Message; } }
            if (!string.IsNullOrEmpty(lastStage) && GUILayout.Button("Locate created stage")) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(lastStage));
            EditorGUILayout.HelpBox(status, MessageType.Info);
            search = EditorGUILayout.TextField("Find asset", search);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(90), GUILayout.MaxHeight(220));
            foreach (var entry in inventory.assets.Where(e => (e.name + e.id).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).Take(100))
            {
                if (GUILayout.Button(entry.name + " · " + entry.kind, EditorStyles.miniButton)) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(entry.path));
                EditorGUILayout.SelectableLabel(entry.id, EditorStyles.miniLabel, GUILayout.Height(16));
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Direct with your API", EditorStyles.boldLabel);
            brief = EditorGUILayout.TextArea(brief, GUILayout.MinHeight(65));
            EditorGUILayout.HelpBox("Configure your API in the full window. Generate sends the brief and live manifest to that provider; supplied audio and captions can be edited in the full window. Voice generation and lipsync require compatible assets.", MessageType.None);
            using (new EditorGUI.DisabledScope(busy || string.IsNullOrWhiteSpace(brief)))
                if (GUILayout.Button("Generate and produce first cut")) Run(async () => {
                    await Sync();
                    var path = "projects/" + ProjectId + "/documents/default";
                    var current = await DirectorWorkbenchClient.Call(path, null, lifetime.Token);
                    var reply = await DirectorWorkbenchClient.Operation("direct", new JObject { ["projectId"] = ProjectId, ["brief"] = brief, ["currentFilm"] = current["film"], ["render"] = false }, lifetime.Token);
                    var saved = await DirectorWorkbenchClient.Operation(path, new JObject { ["expectedRevision"] = current["revision"], ["film"] = reply["film"] }, lifetime.Token);
                    var job = await DirectorWorkbenchClient.Operation(path + "/produce", new JObject { ["revision"] = saved["revision"], ["preview"] = false }, lifetime.Token);
                    status = "Production queued: " + (string)job["id"];
                });
            if (!string.IsNullOrEmpty(jobs)) EditorGUILayout.HelpBox(jobs, MessageType.None);
        }
    }
}
