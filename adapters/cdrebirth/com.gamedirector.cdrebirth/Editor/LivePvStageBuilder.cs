using System;
using System.IO;
using System.Linq;
using GameDirector.Core.Dsl;
using GameDirector.Unity;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.CDREBIRTH.Editor
{
    /// <summary>Versioned cinematic art direction. Faces use native Animator
    /// curves on independent materials, never gameplay UI cameras or wall time.</summary>
    public static class LivePvStageBuilder
    {
        public const string ScenePath="Assets/Scenes/GameDirector/GD_CAMDOWN_LivePV_Sandbox.unity";
        const string SourceScene="Assets/Scenes/GameDirector/GD_CAMDOWN_PV_Sandbox.unity";
        const string Root="Assets/Scenes/GameDirector/LivePV";
        const string Marker="LivePV_Stage_Committed_v1";
        public static string Configure()
        {
            var s=SceneManager.GetActiveScene();
            if(EditorApplication.isPlaying || EditorApplication.isCompiling || s.isDirty || SceneManager.sceneCount!=1 || (s.path!=SourceScene && s.path!=ScenePath))
                throw new InvalidOperationException("Open the clean PV source or live PV copy in Edit mode");
            if(s.path==ScenePath && s.GetRootGameObjects().Any(g=>g.name==Marker)) { Validate(s);return "Live PV stage already committed"; }
            if(AssetDatabase.IsValidFolder(Root) || File.Exists(ScenePath))throw new InvalidOperationException("Preserve occupied live PV outputs");
            PvStageBuilder.ValidateConfigured(s);
            var shader=Shader.Find("GameDirector/LED Face");
            var orange=AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/Character/Orange/M_character_orange.mat");
            var blue=AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/Character/Blue/M_character.mat");
            if(shader==null || orange==null || blue?.mainTexture==null)throw new InvalidOperationException("Missing face shader or costume source");
            foreach(var id in new[]{"hero","crew"})if(!DirectorRoleRegistry.Find(id,s).GetComponentsInChildren<MeshRenderer>(true).Any(r=>r.name=="face"))throw new InvalidOperationException("Missing face: "+id);
            if(!EditorSceneManager.SaveScene(s,ScenePath,true))throw new InvalidOperationException("Scene copy failed");
            s=EditorSceneManager.OpenScene(ScenePath);
            try {
                AssetDatabase.CreateFolder("Assets/Scenes/GameDirector","LivePV");
                // A matched cinematic material family avoids mixing the game's
                // experimental toon outline with the partner's PBR costume.
                // Original production materials and texture assets stay intact.
                var blueCostume=new Material(orange){name="LivePV_BlueCostume"};
                blueCostume.mainTexture=blue.mainTexture;blueCostume.SetFloat("_Smoothness",.2f);
                AssetDatabase.CreateAsset(blueCostume,Root+"/BlueCostume.mat");
                var controller=AnimatorController.CreateAnimatorControllerAtPath(Root+"/Faces.controller");
                foreach(var name in new[]{"Idle","Talk","Worried","Panic"}) {
                    var clip=new AnimationClip{name=name,frameRate=24};
                    float mood=(name=="Worried" || name=="Panic")?1:0;
                    Curve(clip,"_Mood",new[]{new Keyframe(0,mood),new Keyframe(2.4f,mood)});
                    Curve(clip,"_Blink",new[]{new Keyframe(0,0),new Keyframe(1.8f,0),new Keyframe(1.86f,1),new Keyframe(1.96f,0),new Keyframe(2.4f,0)});
                    var talk=name=="Talk" || name=="Panic";
                    Curve(clip,"_MouthOpen",talk?Enumerable.Range(0,25).Select(i=>new Keyframe(i*.1f,i%3==0?.08f:(i%2==0?.65f:.35f))).ToArray():new[]{new Keyframe(0,0),new Keyframe(2.4f,0)});
                    var settings=AnimationUtility.GetAnimationClipSettings(clip);settings.loopTime=true;AnimationUtility.SetAnimationClipSettings(clip,settings);
                    AssetDatabase.CreateAsset(clip,Root+"/Face_"+name+".anim");
                    var state=controller.layers[0].stateMachine.AddState(name);state.motion=clip;
                    if(name=="Idle")controller.layers[0].stateMachine.defaultState=state;
                }
                EditorUtility.SetDirty(controller);AssetDatabase.SaveAssetIfDirty(controller);
                var registry=s.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DirectorRoleRegistry>(true)).Single();
                var reg=new SerializedObject(registry);var entries=reg.FindProperty("entries");
                foreach(var id in new[]{"hero","crew"}) {
                    var actor=DirectorRoleRegistry.Find(id,s);
                    var face=actor.GetComponentsInChildren<MeshRenderer>(true).Single(r=>r.name=="face");
                    var mat=new Material(shader){name=id+"_LED"};mat.SetColor("_FaceColor",id=="hero"?new Color(.52f,.80f,1):new Color(1,.66f,.32f));
                    AssetDatabase.CreateAsset(mat,Root+"/"+id+"_LED.mat");face.sharedMaterial=mat;face.SetPropertyBlock(null);
                    var a=face.gameObject.AddComponent<Animator>();a.runtimeAnimatorController=controller;a.cullingMode=AnimatorCullingMode.AlwaysAnimate;a.fireEvents=false;a.Rebind();a.Play("Idle",0,0);a.Update(0);a.speed=0;
                    int index=entries.arraySize++;var e=entries.GetArrayElementAtIndex(index);e.FindPropertyRelative("roleId").stringValue=id+"_face";e.FindPropertyRelative("target").objectReferenceValue=face.transform;
                    if(id=="crew")foreach(var r in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))if(r.sharedMaterial!=null && r.sharedMaterial.name=="M_character")r.sharedMaterial=orange;
                    if(id=="hero")foreach(var r in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))if(r.sharedMaterial!=null && r.sharedMaterial.name=="M_character")r.sharedMaterial=blueCostume;
                }
                reg.ApplyModifiedPropertiesWithoutUndo();
                var adapter=s.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<CdRebirthAdapter>(true)).Single();
                var adapterSO=new SerializedObject(adapter);var manifestProperty=adapterSO.FindProperty("manifestJson");
                var m=BridgeJson.Deserialize<CapabilityManifest>(((TextAsset)manifestProperty.objectReferenceValue).text);
                var display=new ActorDescriptor{Id="led_face",DisplayName="Independently animated helmet display"};display.Clips.AddRange(new[]{"Idle","Talk","Worried","Panic"});m.Actors.Add(display);
                foreach(var id in new[]{"hero_face","crew_face"})m.Roles.Add(new RoleDescriptor{Id=id,Kind="prop",DefaultActor="led_face",PresentAtStart=true});
                File.WriteAllText(Root+"/live.manifest.json",BridgeJson.Serialize(m));AssetDatabase.ImportAsset(Root+"/live.manifest.json");manifestProperty.objectReferenceValue=AssetDatabase.LoadAssetAtPath<TextAsset>(Root+"/live.manifest.json");adapterSO.ApplyModifiedPropertiesWithoutUndo();
                RenderSettings.ambientLight=new Color(.4f,.46f,.54f);RenderSettings.fogColor=new Color(.025f,.055f,.073f);RenderSettings.fogDensity=.030f;
                foreach(var light in s.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Light>(true))) {
                    if(light.name=="Directional Light"){light.intensity=1.5f;light.color=new Color(.87f,.90f,1);light.transform.rotation=Quaternion.Euler(45,-40,0);light.shadows=LightShadows.Soft;}
                    if(light.name=="GD_WarmFill"){light.intensity=16;light.color=new Color(1,.65f,.35f);light.type=LightType.Spot;light.spotAngle=115;light.range=24;light.shadows=LightShadows.Soft;light.transform.position=new Vector3(-4,5,-5);light.transform.LookAt(new Vector3(0,1,0));}
                    if(light.name=="GD_MoonRim"){light.intensity=22;light.color=new Color(.18f,.72f,1);}
                    if(light.name=="GD_BlueFill")light.intensity=.24f;
                }
                var marker=new GameObject(Marker);SceneManager.MoveGameObjectToScene(marker,s);
                EditorSceneManager.MarkSceneDirty(s);if(!EditorSceneManager.SaveScene(s))throw new InvalidOperationException("Live PV save failed");Validate(s);
                return "Live PV committed: independent animated faces, orange partner, moon and warm camera-side key";
            } catch {
                EditorSceneManager.OpenScene(SourceScene);AssetDatabase.DeleteAsset(ScenePath);AssetDatabase.DeleteAsset(Root);throw;
            }
        }
        static void Curve(AnimationClip clip,string property,Keyframe[] keys) { clip.SetCurve("",typeof(MeshRenderer),"material."+property,new AnimationCurve(keys)); }
        public static void Validate(Scene s) {
            if(s.path!=ScenePath)throw new InvalidOperationException("Unexpected live PV scene");
            foreach(var id in new[]{"hero_face","crew_face"}) {
                var t=DirectorRoleRegistry.Find(id,s);if(t==null || t.GetComponent<Animator>()?.runtimeAnimatorController==null || t.GetComponent<MeshRenderer>().sharedMaterial.shader.name!="GameDirector/LED Face")throw new InvalidOperationException("Incomplete face: "+id);
            }
        }
    }
}
