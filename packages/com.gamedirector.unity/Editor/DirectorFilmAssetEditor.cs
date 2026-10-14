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
        private string message = "";
        private bool busy, advanced;
        private string scriptDraft, scriptBase;
        private CancellationTokenSource lifetime;
        private double nextPoll;
        private string StateKey => "GameDirector.Document." + DirectorWorkbenchClient.StoreId + "." + DirectorWorkbenchClient.ProjectId + "." + Asset.DocumentId;

        private void OnEnable()
        {
            lifetime = new CancellationTokenSource(); if (message == "") message = DirectorUi.T("startMessage"); Undo.undoRedoPerformed += Repaint;
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
            message = DirectorUi.T("filmConnected");
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
                if (JToken.DeepEquals(Asset.Film, submitted)) Edit((JObject)value["film"].DeepClone(), DirectorUi.T("undoSave"));
                AssetDatabase.SaveAssetIfDirty(Asset);
            }
            message = DirectorUi.T("revisionSaved", value["revision"]);
            return value;
        }
        private async Task Produce(bool preview)
        {
            var saved = await Save(false); var path = DirectorWorkbenchClient.DocumentPath(Asset);
            readiness = await DirectorWorkbenchClient.Call(path + "/readiness", new { revision = (long)saved["revision"], preview }, lifetime.Token);
            if (!(bool)readiness["ready"]) { message = DirectorUi.T("notReady"); return; }
            var job = await DirectorWorkbenchClient.Operation(path + "/produce", new JObject { ["revision"] = saved["revision"], ["preview"] = preview }, lifetime.Token);
            message = DirectorUi.T("productionSaved", job["id"]); await Refresh();
        }
        public override void OnInspectorGUI()
        {
            var film = Asset.Film;
            EditorGUILayout.LabelField(DirectorUi.T("filmHeader"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(document == null ? DirectorUi.T("notConnected") : DirectorUi.T("revisionLabel", document["revision"], JToken.DeepEquals(film, document["film"]) ? DirectorUi.T("syncedSuffix") : DirectorUi.T("localEditsSuffix")));
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button(DirectorUi.T("startWorkbench"))) Run(async () => { await DirectorWorkbenchLauncher.EnsureStarted(lifetime.Token); await Connect(); });
                if (GUILayout.Button(DirectorUi.T("openFilm"))) Run(() => DirectorWorkbenchClient.OpenBrowser(Asset, lifetime.Token));
                if (GUILayout.Button(DirectorUi.T("createExample"))) DirectorExample.Create();
            }
            if (Conflict)
            {
                EditorGUILayout.HelpBox(DirectorUi.T("conflict", remote["revision"]), MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(busy || remote?["film"] == null))
                if (GUILayout.Button(DirectorUi.T("loadRevision"))) { Edit((JObject)remote["film"].DeepClone(), DirectorUi.T("undoLoad")); Remember((JObject)remote.DeepClone()); }
            if (document != null)
            {
                foreach (var suffix in new[] { "", "/produce" })
                {
                    var pendingPath = DirectorWorkbenchClient.DocumentPath(Asset) + suffix;
                    var pending = DirectorWorkbenchClient.Pending(pendingPath);
                    if (pending == null) continue;
                    using (new EditorGUI.DisabledScope(busy))
                        if (GUILayout.Button(suffix == "" ? DirectorUi.T("confirmSave") : DirectorUi.T("confirmTake"))) Run(async () => {
                            var receipt = await DirectorWorkbenchClient.Operation(pendingPath, pending, lifetime.Token);
                            if (suffix == "") Remember((JObject)receipt);
                            await Refresh(); message = DirectorUi.T("operationConfirmed");
                        });
                }
            }
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            film["title"] = EditorGUILayout.TextField(DirectorUi.T("filmTitle"), (string)film["title"]);
            film["frameRate"] = EditorGUILayout.IntField(DirectorUi.T("frameRate"), (int?)film["frameRate"] ?? GameDirector.Core.Dsl.FilmDefaults.FrameRate);
            film["width"] = EditorGUILayout.IntField(DirectorUi.T("width"), (int?)film["width"] ?? GameDirector.Core.Dsl.FilmDefaults.Width);
            film["height"] = EditorGUILayout.IntField(DirectorUi.T("height"), (int?)film["height"] ?? GameDirector.Core.Dsl.FilmDefaults.Height);
            if (EditorGUI.EndChangeCheck()) Edit(film, DirectorUi.T("undoSettings"));
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stageModel"), new GUIContent(DirectorUi.T("leadPrefab")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stage"), new GUIContent(DirectorUi.T("stage")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("suppliedSound"), new GUIContent(DirectorUi.T("suppliedSound")));
            serializedObject.ApplyModifiedProperties();
            using (new EditorGUI.DisabledScope(busy || Asset.stageModel == null || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(DirectorUi.T("createLeadStage")))
                {
                    try { var path = DirectorStageFactory.Create(Asset.stageModel); Undo.RecordObject(Asset, DirectorUi.T("undoBind")); Asset.stage = AssetDatabase.LoadAssetAtPath<SceneAsset>(path); EditorUtility.SetDirty(Asset); message = DirectorUi.T("stageCreated"); }
                    catch (Exception ex) { message = ex.Message; }
                }
            if (Asset.stage != null && GUILayout.Button(DirectorUi.T("locateStage"))) EditorGUIUtility.PingObject(Asset.stage);
            using (new EditorGUI.DisabledScope(busy || Asset.suppliedSound == null))
                if (GUILayout.Button(DirectorUi.T("importMusic"))) Run(ImportSound);
            EditorGUILayout.Space(); EditorGUILayout.LabelField(DirectorUi.T("shots"), EditorStyles.boldLabel);
            foreach (var scene in ((JArray)film["scenes"]).ToArray())
            {
                EditorGUILayout.LabelField((string)scene["id"], EditorStyles.miniBoldLabel);
                var shots = (JArray)scene["shots"];
                foreach (var shot in shots.ToArray())
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox); EditorGUILayout.LabelField((string)shot["id"]);
                    EditorGUI.BeginChangeCheck();
                    shot["purpose"] = EditorGUILayout.TextField(DirectorUi.T("purpose"), (string)shot["purpose"]);
                    shot["start"] = EditorGUILayout.DoubleField(DirectorUi.T("startTime"), (double)shot["start"]);
                    shot["end"] = EditorGUILayout.DoubleField(DirectorUi.T("endTime"), (double)shot["end"]);
                    var camera = (JObject)shot["camera"];
                    camera["subject"] = Choice(DirectorUi.T("role"), (string)camera["subject"], manifest?["roles"]?.Select(x => (string)x["id"]).ToArray());
                    camera["from"] = Choice(DirectorUi.T("anchor"), (string)camera["from"], manifest?["locations"]?.Select(x => (string)x["id"]).ToArray());
                    camera["type"] = Choice(DirectorUi.T("cameraMove"), (string)camera["type"], manifest?["shotTypes"]?.Select(x => (string)x).ToArray());
                    camera["frame"] = Choice(DirectorUi.T("frame"), (string)camera["frame"], manifest?["frameTypes"]?.Select(x => (string)x).ToArray());
                    if (EditorGUI.EndChangeCheck()) Edit(film, DirectorUi.T("undoShot"));
                    if (GUILayout.Button(DirectorUi.T("removeShot"))) { shot.Remove(); Edit(film, DirectorUi.T("undoRemove")); }
                    EditorGUILayout.EndVertical();
                }
                if (GUILayout.Button(DirectorUi.T("addShot"))) { shots.Add(NewShot()); Edit(film, DirectorUi.T("undoAdd")); }
            }
            if (GUILayout.Button(DirectorUi.T("addScene"))) { ((JArray)film["scenes"]).Add(new JObject { ["id"] = "scene-" + Guid.NewGuid().ToString("N"), ["performance"] = new JArray(), ["shots"] = new JArray(NewShot()) }); Edit(film, DirectorUi.T("undoScene")); }
            advanced = EditorGUILayout.Foldout(advanced, DirectorUi.T("revise"), true);
            if (advanced)
            {
                var currentScript = film.ToString();
                if (scriptDraft == null || scriptDraft == scriptBase) { scriptDraft = currentScript; scriptBase = currentScript; }
                scriptDraft = EditorGUILayout.TextArea(scriptDraft, GUILayout.MinHeight(150));
                if (scriptBase != currentScript) EditorGUILayout.HelpBox(DirectorUi.T("scriptChanged"), MessageType.Warning);
                if (GUILayout.Button(DirectorUi.T("loadScript"))) { scriptDraft = currentScript; scriptBase = currentScript; }
                using (new EditorGUI.DisabledScope(scriptBase != currentScript))
                    if (GUILayout.Button(DirectorUi.T("applyScript"))) { try { var parsed = JObject.Parse(scriptDraft); if ((int?)parsed["version"] != 1 || !(parsed["scenes"] is JArray)) throw new ArgumentException(DirectorUi.T("invalidScript")); Edit(parsed, DirectorUi.T("undoScript")); scriptDraft = parsed.ToString(); scriptBase = scriptDraft; } catch (Exception ex) { message = ex.Message; } }
            }
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button(DirectorUi.T("saveRevision"))) Run(async () => { await Save(); });
                if (GUILayout.Button(DirectorUi.T("checkReadiness"))) Run(async () => { var value = await Save(false); readiness = await DirectorWorkbenchClient.Call(DirectorWorkbenchClient.DocumentPath(Asset) + "/readiness", new { revision = (long)value["revision"] }, lifetime.Token); });
                if (GUILayout.Button(DirectorUi.T("makePreview"))) Run(() => Produce(true));
                if (GUILayout.Button(DirectorUi.T("makeFilm"))) Run(() => Produce(false));
                if (GUILayout.Button(DirectorUi.T("exportScript")))
                { var path = EditorUtility.SaveFilePanel(DirectorUi.T("exportTitle"), "", Asset.name + ".film", "json"); if (path != "") File.WriteAllText(path, Asset.Film.ToString()); }
            }
            if (readiness != null) foreach (var check in readiness["checks"])
                EditorGUILayout.HelpBox(DirectorUi.ServerMessage((string)check["code"], (string)check["message"], check["messageArgs"]) + "\n" + DirectorUi.ServerMessage((string)check["actionCode"], (string)check["action"], check["messageArgs"]), (bool)check["ready"] ? MessageType.Info : MessageType.Warning);
            if (jobs != null) foreach (var job in jobs)
            {
                var state = (string)job["state"]; var id = (string)job["id"];
                EditorGUILayout.HelpBox((string)job["request"]["film"]["title"] + " · " + DirectorUi.State(state) + "\n" + (string)job["progress"], MessageType.None);
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (state == "Verified") { if (GUILayout.Button(DirectorUi.T("reviewFilm"))) Run(() => DirectorWorkbenchClient.OpenBrowser(Asset, lifetime.Token)); }
                    else if (GUILayout.Button(state == "Running" || state == DirectorUi.T("state.Queued") ? DirectorUi.T("stopProduction") : DirectorUi.T("resumeProduction"))) Run(async () => { await DirectorWorkbenchClient.Call("jobs/" + id + (state == "Running" || state == DirectorUi.T("state.Queued") ? "/cancel" : "/resume"), new { }, lifetime.Token); await Refresh(); });
                }
            }
            EditorGUILayout.HelpBox(message, MessageType.Info);
        }
        private async Task ImportSound()
        {
            await DirectorWorkbenchClient.Register(lifetime.Token);
            var path = AssetDatabase.GetAssetPath(Asset.suppliedSound); var bytes = File.ReadAllBytes(path);
            if (bytes.Length > GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes) throw new ArgumentException(DirectorUi.T("soundLimit", GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes / (1024 * 1024)));
            var sound = await DirectorWorkbenchClient.Call("projects/" + DirectorWorkbenchClient.ProjectId + "/media", new { name = Path.GetFileName(path), base64 = Convert.ToBase64String(bytes) }, lifetime.Token);
            var film = Asset.Film; var duration = ((JArray)film["scenes"]).SelectMany(s => (JArray)s["shots"]).Sum(s => (double)s["end"] - (double)s["start"]);
            ((JArray)film["audio"]).Add(new JObject { ["id"] = "sound-" + Guid.NewGuid().ToString("N"), ["mediaId"] = sound["id"], ["bus"] = "music", ["at"] = 0,
                ["sourceStart"] = 0, ["duration"] = Math.Min(duration, (double)sound["duration"]), ["volume"] = .5, ["fadeIn"] = 0, ["fadeOut"] = 0 });
            Edit(film, DirectorUi.T("undoSound")); message = DirectorUi.T("soundImported");
        }
        private static string Choice(string label, string current, string[] choices)
        {
            if (choices == null || choices.Length == 0) return EditorGUILayout.TextField(label, current ?? "");
            var all = new[] { current ?? "" }.Concat(choices).Distinct().ToArray();
            return all[EditorGUILayout.Popup(label, 0, all)];
        }
        private static JObject NewShot() => new JObject { ["id"] = "shot-" + Guid.NewGuid().ToString("N"), ["start"] = 0, ["end"] = 3,
            ["purpose"] = DirectorUi.AuthoredShotPurpose, ["camera"] = new JObject { ["type"] = GameDirector.Core.Dsl.ShotVocabulary.DefaultShotId,
                ["subject"] = DirectorStageVocabulary.LeadRoleId, ["from"] = DirectorStageVocabulary.WideAnchor, ["frame"] = GameDirector.Core.Dsl.ShotVocabulary.DefaultFrameId } };
    }
}
