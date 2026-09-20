using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.Unity.Editor
{
    // Entirely generated from primitives, curves and synthesized samples.
    public static class DirectorExample
    {
        [MenuItem("Tools/GameDirector/Create Independent Example")]
        public static void Create() { Selection.activeObject = CreateAsset(); }
        public static DirectorFilmAsset CreateAsset()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before creating example assets.");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.IsNullOrEmpty(scene.path) && !EditorSceneManager.IsPreviewScene(scene))
                    throw new InvalidOperationException("Save or close the untitled scene before creating the example. No example assets were written.");
            }
            var folder = AssetDatabase.GenerateUniqueAssetPath("Assets/GameDirector Example");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var root = new GameObject("Example performer");
            try
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule); body.name = "Body"; body.transform.SetParent(root.transform, false);
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                var material = new Material(shader) { color = new Color(.92f, .32f, .12f) };
                AssetDatabase.CreateAsset(material, folder + "/Performer.mat"); body.GetComponent<Renderer>().sharedMaterial = material;
                var clip = new AnimationClip { name = "greet", frameRate = 24 };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.y"),
                    new AnimationCurve(new Keyframe(0, 0), new Keyframe(.5f, .3f), new Keyframe(1, 0), new Keyframe(1.5f, .3f), new Keyframe(2, 0), new Keyframe(3, 0)));
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings);
                AssetDatabase.CreateAsset(clip, folder + "/Greet.anim");
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Performer.controller");
                var state = controller.AddMotion(clip); root.AddComponent<Animator>().runtimeAnimatorController = controller;
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Performer.prefab");
                var stagePath = DirectorStageFactory.Create(prefab);
                File.WriteAllBytes(folder + "/Example sound.wav", Sound()); AssetDatabase.ImportAsset(folder + "/Example sound.wav");
                var asset = ScriptableObject.CreateInstance<DirectorFilmAsset>(); asset.stageModel = prefab;
                asset.stage = AssetDatabase.LoadAssetAtPath<SceneAsset>(stagePath); asset.suppliedSound = AssetDatabase.LoadAssetAtPath<AudioClip>(folder + "/Example sound.wav");
                var film = ExampleFilm();
                film["scenes"][0]["performance"][0]["clip"] = controller.layers[0].name + "." + state.name;
                asset.Film = film; AssetDatabase.CreateAsset(asset, folder + "/First Film.asset");
                return asset;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        public static JObject ExampleFilm()
        {
            // Names come from the stage vocabulary; the clip id is filled in by
            // the caller from the actually created Animator state.
            var template = @"{
              'version':1,'title':'第一次登场','frameRate':$FPS,'width':$W,'height':$H,
              'scenes':[{'id':'greeting','performance':[{'t':0,'type':'actor.anim','role':'$ROLE','clip':'$CLIP','fade':0}],
              'shots':[
              {'id':'wide','start':0,'end':1,'purpose':'介绍角色与空间','camera':{'type':'$SHOT','subject':'$ROLE','from':'$WIDE','frame':'$FRAME'}},
              {'id':'close','start':1,'end':2,'purpose':'靠近观察角色的回应','camera':{'type':'$SHOT','subject':'$ROLE','from':'$CLOSE','frame':'closeup'}},
              {'id':'side','start':2,'end':3,'purpose':'从侧面收束表演','camera':{'type':'$SHOT','subject':'$ROLE','from':'$SIDE','frame':'$FRAME'}}]}],
              'audio':[],'subtitles':[{'start':0,'end':1.5,'text':'第一次登场'},{'start':1.5,'end':3,'text':'每个镜头，都可以继续修改。'}]}";
            return JObject.Parse(template
                // Longest tokens first: "$W" is a prefix of "$WIDE", "$H" of "$SHOT".
                .Replace("$WIDE", DirectorStageVocabulary.WideAnchor)
                .Replace("$CLOSE", DirectorStageVocabulary.CloseAnchor)
                .Replace("$SIDE", DirectorStageVocabulary.SideAnchor)
                .Replace("$SHOT", GameDirector.Core.Dsl.ShotVocabulary.DefaultShotId)
                .Replace("$FRAME", GameDirector.Core.Dsl.ShotVocabulary.DefaultFrameId)
                .Replace("$ROLE", DirectorStageVocabulary.LeadRoleId)
                .Replace("$CLIP", "Base Layer.Greet")
                .Replace("$FPS", GameDirector.Core.Dsl.FilmDefaults.FrameRate.ToString())
                .Replace("$W", GameDirector.Core.Dsl.FilmDefaults.Width.ToString())
                .Replace("$H", GameDirector.Core.Dsl.FilmDefaults.Height.ToString()));
        }
        private static byte[] Sound()
        {
            const int rate = 48000, count = rate * 3;
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                var notes = new[] { 261.63, 329.63, 392.0, 523.25, 392.0, 329.63 };
                for (var i = 0; i < count; i++)
                {
                    var time = (double)i / rate; var phase = time % .5; var envelope = Math.Min(1, phase * 50) * Math.Max(0, 1 - phase * 2);
                    writer.Write((short)(Math.Sin(time * notes[Math.Min(5, (int)(time * 2))] * Math.PI * 2) * envelope * 4500));
                }
                return memory.ToArray();
            }
        }
    }
}
