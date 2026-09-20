using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    [CustomEditor(typeof(DirectorFilmAsset))]
    public sealed class DirectorFilmAssetEditor : UnityEditor.Editor
    {
        private DirectorFilmAsset Asset => (DirectorFilmAsset)target;
        private JObject document, remote, manifest;
        private JToken readiness, jobs;
        private string message = "启动工作台后连接作品。可以先编辑并保存 Unity 资产。";
        private bool busy, advanced;
        private string scriptDraft, scriptBase;
        private CancellationTokenSource lifetime;
        private double nextPoll;
        private string StateKey => "GameDirector.Document." + DirectorWorkbenchClient.StoreId + "." + DirectorWorkbenchClient.ProjectId + "." + Asset.DocumentId;

        private void OnEnable()
        {
            lifetime = new CancellationTokenSource(); Undo.undoRedoPerformed += Repaint;
            EditorApplication.update += Poll;
        }
        private void OnDisable() { EditorApplication.update -= Poll; Undo.undoRedoPerformed -= Repaint; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
        private void Poll()
        {
            if (document == null || busy || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 3;
            Run(Refresh);
        }
        private async void Run(Func<Task> action)
        {
            busy = true;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (DirectorServiceException ex)
            {
                if (ex.Detail["current"] is JObject current) remote = current;
                message = ex.Message;
            }
            catch (Exception ex) { message = ex.Message; }
            finally { busy = false; if (this != null) Repaint(); }
        }
        private void Remember(JObject value) { document = value; EditorPrefs.SetString(StateKey, value.ToString()); }
        private bool Conflict => remote != null && document != null && (long)remote["revision"] != (long)document["revision"];
        private void Edit(JObject film, string label)
        {
            Undo.RecordObject(Asset, label); Asset.Film = film; EditorUtility.SetDirty(Asset);
        }
        private async Task Refresh()
        {
            var value = (JObject)await DirectorWorkbenchClient.Call(DirectorWorkbenchClient.DocumentPath(Asset), null, lifetime.Token);
            if (document == null)
            {
                // An unlinked asset must not adopt a newer remote base implicitly.
                var initial = (JObject)value.DeepClone();
                if ((long)value["revision"] > 0 && !JToken.DeepEquals(Asset.Film, value["film"])) { initial["revision"] = 0; initial["film"] = null; }
                Remember(initial);
            }
            remote = value;
            var state = await DirectorWorkbenchClient.Call("state", null, lifetime.Token);
            var project = state["projects"].FirstOrDefault(p => (string)p["id"] == DirectorWorkbenchClient.ProjectId);
            manifest = project?["manifest"] as JObject;
            jobs = new JArray(state["jobs"].Where(j => (string)j["request"]["projectId"] == DirectorWorkbenchClient.ProjectId &&
                (string)j["request"]["documentId"] == Asset.DocumentId).Take(5));
        }
        private async Task Connect()
        {
            await DirectorWorkbenchClient.Register(lifetime.Token);
            var saved = EditorPrefs.GetString(StateKey, "");
            document = saved == "" ? null : JObject.Parse(saved);
            await Refresh();
            message = "作品已连接。Unity 资产与工作台草稿通过明确的保存/载入操作同步。";
        }
        private async Task<JObject> Save(bool saveUnityAsset = true)
        {
            if (document == null) await Connect();
            var submitted = Asset.Film;
            var value = (JObject)await DirectorWorkbenchClient.Operation(DirectorWorkbenchClient.DocumentPath(Asset),
                new JObject { ["expectedRevision"] = document["revision"], ["film"] = submitted }, lifetime.Token);
            Remember(value); remote = value;
            // Do not replace a local edit that arrived while the request was in flight.
            if (saveUnityAsset)
            {
                if (JToken.DeepEquals(Asset.Film, submitted)) Edit((JObject)value["film"].DeepClone(), "Save director document");
                AssetDatabase.SaveAssetIfDirty(Asset);
            }
            message = "已保存作品版本 " + value["revision"] + "，旧版本保留。";
            return value;
        }
        private async Task Produce(bool preview)
        {
            var saved = await Save(false); var path = DirectorWorkbenchClient.DocumentPath(Asset);
            readiness = await DirectorWorkbenchClient.Call(path + "/readiness", new { revision = (long)saved["revision"], preview }, lifetime.Token);
            if (!(bool)readiness["ready"]) { message = "请先处理准备检查中的项目。"; return; }
            var job = await DirectorWorkbenchClient.Operation(path + "/produce", new JObject { ["revision"] = saved["revision"], ["preview"] = preview }, lifetime.Token);
            message = "制作任务已保存：" + job["id"]; await Refresh();
        }
        public override void OnInspectorGUI()
        {
            var film = Asset.Film;
            EditorGUILayout.LabelField("GameDirector · 导演作品", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(document == null ? "未连接工作台" : "工作台版本 " + document["revision"] + (JToken.DeepEquals(film, document["film"]) ? " · 已同步" : " · 本地内容有修改"));
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("启动 / 连接工作台")) Run(async () => { await DirectorWorkbenchLauncher.EnsureStarted(lifetime.Token); await Connect(); });
                if (GUILayout.Button("在网页版打开同一作品")) DirectorWorkbenchClient.OpenBrowser(Asset);
                if (GUILayout.Button("创建独立示例（新建资产和场景）")) DirectorExample.Create();
            }
            if (Conflict)
            {
                EditorGUILayout.HelpBox("另一处已保存版本 " + remote["revision"] + "。你的资产没有被覆盖；可先导出本地脚本，或另存为新资产，再载入新版。", MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(busy || remote?["film"] == null))
                if (GUILayout.Button("载入工作台版本（可 Undo）")) { Edit((JObject)remote["film"].DeepClone(), "Load director revision"); Remember((JObject)remote.DeepClone()); }
            if (document != null)
            {
                foreach (var suffix in new[] { "", "/produce" })
                {
                    var pendingPath = DirectorWorkbenchClient.DocumentPath(Asset) + suffix;
                    var pending = DirectorWorkbenchClient.Pending(pendingPath);
                    if (pending == null) continue;
                    using (new EditorGUI.DisabledScope(busy))
                        if (GUILayout.Button(suffix == "" ? "确认上次保存的结果" : "确认上次制作的结果")) Run(async () => {
                            var receipt = await DirectorWorkbenchClient.Operation(pendingPath, pending, lifetime.Token);
                            if (suffix == "") Remember((JObject)receipt);
                            await Refresh(); message = "上次操作的结果已确认；本地编辑内容保留。";
                        });
                }
            }
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            film["title"] = EditorGUILayout.TextField("作品标题", (string)film["title"]);
            film["frameRate"] = EditorGUILayout.IntField("帧率", (int?)film["frameRate"] ?? GameDirector.Core.Dsl.FilmDefaults.FrameRate);
            film["width"] = EditorGUILayout.IntField("宽度", (int?)film["width"] ?? GameDirector.Core.Dsl.FilmDefaults.Width);
            film["height"] = EditorGUILayout.IntField("高度", (int?)film["height"] ?? GameDirector.Core.Dsl.FilmDefaults.Height);
            if (EditorGUI.EndChangeCheck()) Edit(film, "Edit film settings");
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stageModel"), new GUIContent("主角预制体"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stage"), new GUIContent("离线拍摄场景"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("suppliedSound"), new GUIContent("导入声音素材"));
            serializedObject.ApplyModifiedProperties();
            using (new EditorGUI.DisabledScope(busy || Asset.stageModel == null || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("从主角创建拍摄场景（新建）"))
                {
                    try { var path = DirectorStageFactory.Create(Asset.stageModel); Undo.RecordObject(Asset, "Bind director stage"); Asset.stage = AssetDatabase.LoadAssetAtPath<SceneAsset>(path); EditorUtility.SetDirty(Asset); message = "场景已创建。打开该场景并进入播放，再检查拍摄条件。"; }
                    catch (Exception ex) { message = ex.Message; }
                }
            if (Asset.stage != null && GUILayout.Button("定位拍摄场景")) EditorGUIUtility.PingObject(Asset.stage);
            using (new EditorGUI.DisabledScope(busy || Asset.suppliedSound == null))
                if (GUILayout.Button("导入声音并加入配乐轨")) Run(ImportSound);
            EditorGUILayout.Space(); EditorGUILayout.LabelField("镜头", EditorStyles.boldLabel);
            foreach (var scene in ((JArray)film["scenes"]).ToArray())
            {
                EditorGUILayout.LabelField((string)scene["id"], EditorStyles.miniBoldLabel);
                var shots = (JArray)scene["shots"];
                foreach (var shot in shots.ToArray())
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox); EditorGUILayout.LabelField((string)shot["id"]);
                    EditorGUI.BeginChangeCheck();
                    shot["purpose"] = EditorGUILayout.TextField("镜头意图", (string)shot["purpose"]);
                    shot["start"] = EditorGUILayout.DoubleField("表演起点 / 秒", (double)shot["start"]);
                    shot["end"] = EditorGUILayout.DoubleField("终点 / 秒", (double)shot["end"]);
                    var camera = (JObject)shot["camera"];
                    camera["subject"] = Choice("角色", (string)camera["subject"], manifest?["roles"]?.Select(x => (string)x["id"]).ToArray());
                    camera["from"] = Choice("机位", (string)camera["from"], manifest?["locations"]?.Select(x => (string)x["id"]).ToArray());
                    camera["type"] = Choice("运镜", (string)camera["type"], manifest?["shotTypes"]?.Select(x => (string)x).ToArray());
                    camera["frame"] = Choice("景别", (string)camera["frame"], manifest?["frameTypes"]?.Select(x => (string)x).ToArray());
                    if (EditorGUI.EndChangeCheck()) Edit(film, "Edit director shot");
                    if (GUILayout.Button("移除镜头")) { shot.Remove(); Edit(film, "Remove director shot"); }
                    EditorGUILayout.EndVertical();
                }
                if (GUILayout.Button("添加镜头")) { shots.Add(NewShot()); Edit(film, "Add director shot"); }
            }
            if (GUILayout.Button("添加一场表演")) { ((JArray)film["scenes"]).Add(new JObject { ["id"] = "scene-" + Guid.NewGuid().ToString("N"), ["performance"] = new JArray(), ["shots"] = new JArray(NewShot()) }); Edit(film, "Add film scene"); }
            advanced = EditorGUILayout.Foldout(advanced, "表演、声音与字幕脚本", true);
            if (advanced)
            {
                var currentScript = film.ToString();
                if (scriptDraft == null || scriptDraft == scriptBase) { scriptDraft = currentScript; scriptBase = currentScript; }
                scriptDraft = EditorGUILayout.TextArea(scriptDraft, GUILayout.MinHeight(150));
                if (scriptBase != currentScript) EditorGUILayout.HelpBox("作品已修改，脚本输入仍保留。载入当前作品后再继续编辑，避免覆盖。", MessageType.Warning);
                if (GUILayout.Button("载入当前作品脚本")) { scriptDraft = currentScript; scriptBase = currentScript; }
                using (new EditorGUI.DisabledScope(scriptBase != currentScript))
                    if (GUILayout.Button("应用脚本修改")) { try { var parsed = JObject.Parse(scriptDraft); if ((int?)parsed["version"] != 1 || !(parsed["scenes"] is JArray)) throw new ArgumentException("Use a version 1 film."); Edit(parsed, "Edit director script"); scriptDraft = parsed.ToString(); scriptBase = scriptDraft; } catch (Exception ex) { message = ex.Message; } }
            }
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("保存作品版本")) Run(async () => { await Save(); });
                if (GUILayout.Button("检查拍摄条件")) Run(async () => { var value = await Save(false); readiness = await DirectorWorkbenchClient.Call(DirectorWorkbenchClient.DocumentPath(Asset) + "/readiness", new { revision = (long)value["revision"] }, lifetime.Token); });
                if (GUILayout.Button("制作预览")) Run(() => Produce(true));
                if (GUILayout.Button("制作影片")) Run(() => Produce(false));
                if (GUILayout.Button("导出本地拍摄脚本"))
                { var path = EditorUtility.SaveFilePanel("Export film", "", Asset.name + ".film", "json"); if (path != "") File.WriteAllText(path, Asset.Film.ToString()); }
            }
            if (readiness != null) foreach (var check in readiness["checks"])
                EditorGUILayout.HelpBox((string)check["message"] + "\n" + (string)check["action"], (bool)check["ready"] ? MessageType.Info : MessageType.Warning);
            if (jobs != null) foreach (var job in jobs)
            {
                var state = (string)job["state"]; var id = (string)job["id"];
                EditorGUILayout.HelpBox((string)job["request"]["film"]["title"] + " · " + state + "\n" + (string)job["progress"], MessageType.None);
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (state == "Verified") { if (GUILayout.Button("查看成片")) Application.OpenURL(DirectorWorkbenchClient.Address + "/api/jobs/" + id + "/video"); }
                    else if (GUILayout.Button(state == "Running" || state == "Queued" ? "停止制作" : "恢复制作")) Run(async () => { await DirectorWorkbenchClient.Call("jobs/" + id + (state == "Running" || state == "Queued" ? "/cancel" : "/resume"), new { }, lifetime.Token); await Refresh(); });
                }
            }
            EditorGUILayout.HelpBox(message, MessageType.Info);
        }
        private async Task ImportSound()
        {
            await DirectorWorkbenchClient.Register(lifetime.Token);
            var path = AssetDatabase.GetAssetPath(Asset.suppliedSound); var bytes = File.ReadAllBytes(path);
            if (bytes.Length > GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes) throw new ArgumentException("Audio import limit is " + GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes / (1024 * 1024) + " MiB.");
            var sound = await DirectorWorkbenchClient.Call("projects/" + DirectorWorkbenchClient.ProjectId + "/media", new { name = Path.GetFileName(path), base64 = Convert.ToBase64String(bytes) }, lifetime.Token);
            var film = Asset.Film; var duration = ((JArray)film["scenes"]).SelectMany(s => (JArray)s["shots"]).Sum(s => (double)s["end"] - (double)s["start"]);
            ((JArray)film["audio"]).Add(new JObject { ["id"] = "sound-" + Guid.NewGuid().ToString("N"), ["mediaId"] = sound["id"], ["bus"] = "music", ["at"] = 0,
                ["sourceStart"] = 0, ["duration"] = Math.Min(duration, (double)sound["duration"]), ["volume"] = .5, ["fadeIn"] = 0, ["fadeOut"] = 0 });
            Edit(film, "Import director sound"); message = "声音已复制到工作台，并加入本地配乐轨。";
        }
        private static string Choice(string label, string current, string[] choices)
        {
            if (choices == null || choices.Length == 0) return EditorGUILayout.TextField(label, current ?? "");
            var all = new[] { current ?? "" }.Concat(choices).Distinct().ToArray();
            return all[EditorGUILayout.Popup(label, 0, all)];
        }
        private static JObject NewShot() => new JObject { ["id"] = "shot-" + Guid.NewGuid().ToString("N"), ["start"] = 0, ["end"] = 3,
            ["purpose"] = "介绍角色与空间", ["camera"] = new JObject { ["type"] = GameDirector.Core.Dsl.ShotVocabulary.DefaultShotId,
                ["subject"] = DirectorStageVocabulary.LeadRoleId, ["from"] = DirectorStageVocabulary.WideAnchor, ["frame"] = GameDirector.Core.Dsl.ShotVocabulary.DefaultFrameId } };
    }
}
