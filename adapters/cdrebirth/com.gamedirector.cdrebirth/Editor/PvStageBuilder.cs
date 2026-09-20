using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GameDirector.Core.Dsl;
using GameDirector.Unity;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.CDREBIRTH.Editor
{
    /// <summary>Authors cast, real motions and a physical camera in the dedicated
    /// PV stage. The ordinary timeline and capture pipeline directs the result.</summary>
    public static class PvStageBuilder
    {
        private const string Root="Assets/Scenes/GameDirector/PV";
        private const string ScenePath="Assets/Scenes/GameDirector/GD_CAMDOWN_PV_Sandbox.unity";
        public const string CompletionMarker="PV_Stage_Committed_v1";
        private const string Receipt="PV stage v1 committed: two performers, physical camera, humanoid motions";
        private static readonly string[] Outputs={Root+"/PV_Streamer.controller",Root+"/PV_RecordLamp.mat",Root+"/pv.manifest.json"};
        public static string Configure()
        {
            var scene=SceneManager.GetActiveScene();
            CheckContext(scene);
            if(scene.GetRootGameObjects().Any(g=>g.name==CompletionMarker)) {
                ValidateConfigured(scene);return Receipt;
            }
            if(scene.GetRootGameObjects().Any(g=>g.name=="PV_Crew") || Outputs.Any(p=>File.Exists(p) || File.Exists(p+".meta")))
                throw new InvalidOperationException("Uncommitted PV assets exist; inspect them before rebuilding");
            PreflightInputs(scene);
            bool folderExisted=AssetDatabase.IsValidFolder(Root);
            try {
                Build(scene);
                ValidateConfigured(scene);
                var marker=new GameObject(CompletionMarker);SceneManager.MoveGameObjectToScene(marker,scene);
                EditorSceneManager.MarkSceneDirty(scene);
                if(!EditorSceneManager.SaveScene(scene))throw new InvalidOperationException("PV stage save failed");
                return Receipt;
            } catch {
                // Only this previously clean, dedicated scene was mutated. Reload
                // its saved state additively so other open scenes remain untouched.
                Scene restored;
                if(SceneManager.sceneCount==1)restored=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                else {
                    if(!EditorSceneManager.CloseScene(scene,true))throw new InvalidOperationException("PV rollback could not close its scene");
                    restored=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Additive);
                }
                SceneManager.SetActiveScene(restored);
                foreach(var path in Outputs)if(File.Exists(path))AssetDatabase.DeleteAsset(path);
                if(!folderExisted && AssetDatabase.IsValidFolder(Root))AssetDatabase.DeleteAsset(Root);
                throw;
            }
        }
        private static void CheckContext(Scene scene) {
            if(EditorApplication.isPlaying || EditorApplication.isCompiling || scene.isDirty || scene.path!=ScenePath)
                throw new InvalidOperationException("Open the clean dedicated PV copy in Edit mode");
        }
        private static void PreflightInputs(Scene scene) {
            var hero=scene.GetRootGameObjects().Single(g=>g.name=="GD_Hero");
            var a=hero.GetComponentInChildren<Animator>(true);
            if(a==null || !a.isHuman || a.GetBoneTransform(HumanBodyBones.RightHand)==null || !(a.runtimeAnimatorController is AnimatorController))
                throw new InvalidOperationException("PV requires an authored humanoid performer/controller/right hand");
            foreach(var file in new[]{"Idle.fbx","Walk.fbx","Run.fbx","IpadIdle.fbx","IpadRun.fbx"})
                if(!AssetDatabase.LoadAllAssetsAtPath("Assets/Art/Characters/LegacyCharacterRoot/Models/AnimationClips/"+file).OfType<AnimationClip>().Any(c=>c.humanMotion && !c.name.StartsWith("__preview__")))
                    throw new InvalidOperationException("Missing humanoid motion: "+file);
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Wieldables/WieldableCamera.prefab");
            if(prefab==null || !prefab.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="AimRightHandGrip") || prefab.transform.Find("camera_new")==null)
                throw new InvalidOperationException("Missing physical camera model/grip");
            if(Shader.Find("Universal Render Pipeline/Lit")==null)throw new InvalidOperationException("Missing PV light shader");
            scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DirectorRoleRegistry>(true)).Single();
            scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DirectorLocationRegistry>(true)).Single();
            var adapter=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<CdRebirthAdapter>(true)).Single();
            var asset=(TextAsset)new SerializedObject(adapter).FindProperty("manifestJson").objectReferenceValue;
            var manifest=BridgeJson.Deserialize<CapabilityManifest>(asset.text);
            if(manifest.FindRole("hero")==null || manifest.FindActor(manifest.FindRole("hero").DefaultActor)==null || manifest.FindRole("crew")!=null || manifest.FindRole("camera")!=null)
                throw new InvalidOperationException("PV requires the original film manifest with a single borrowed performer");
        }
        public static void ValidateConfigured(Scene scene) {
            if(scene.path!=ScenePath)throw new InvalidOperationException("Unexpected PV scene");
            foreach(var path in Outputs)if(AssetDatabase.LoadMainAssetAtPath(path)==null)throw new InvalidOperationException("Missing committed asset: "+path);
            var hero=DirectorRoleRegistry.Find("hero",scene);var crew=DirectorRoleRegistry.Find("crew",scene);var camera=DirectorRoleRegistry.Find("camera",scene);
            if(hero==null || crew==null || camera==null || crew==hero || !camera.IsChildOf(hero))throw new InvalidOperationException("Incomplete PV cast/attachment");
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(Outputs[0]);
            foreach(var role in new[]{hero,crew}) {
                var a=role.GetComponentInChildren<Animator>(true);
                if(a==null || a.runtimeAnimatorController!=controller)throw new InvalidOperationException("PV controller binding changed");
            }
            foreach(var name in new[]{"PV_Idle","PV_Walk","PV_Run","PV_CameraIdle","PV_CameraRun"})
                if(!controller.layers[0].stateMachine.states.Any(s=>s.state.name==name && s.state.motion!=null))throw new InvalidOperationException("Missing PV motion: "+name);
            var adapter=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<CdRebirthAdapter>(true)).Single();
            var bound=(TextAsset)new SerializedObject(adapter).FindProperty("manifestJson").objectReferenceValue;
            if(AssetDatabase.GetAssetPath(bound)!=Outputs[2])throw new InvalidOperationException("PV manifest binding changed");
            var m=BridgeJson.Deserialize<CapabilityManifest>(bound.text);
            foreach(var id in new[]{"hero","crew","camera"})if(m.FindRole(id)==null || !m.FindRole(id).PresentAtStart || m.FindActor(m.FindRole(id).DefaultActor)==null)throw new InvalidOperationException("Invalid PV role: "+id);
            if(camera.GetComponentsInChildren<Renderer>().Length==0 || camera.GetComponentsInChildren<MonoBehaviour>(true).Length!=0)
                throw new InvalidOperationException("Physical camera presentation invalid");
        }
        private static void Build(Scene scene)
        {
            if(!AssetDatabase.IsValidFolder(Root))AssetDatabase.CreateFolder("Assets/Scenes/GameDirector","PV");
            var hero=scene.GetRootGameObjects().Single(g=>g.name=="GD_Hero");
            var animator=hero.GetComponentInChildren<Animator>(true);
            string controllerPath=Root+"/PV_Streamer.controller";
            if(AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)!=null)
                throw new InvalidOperationException("PV controller already exists; preserve/review partial authoring");
            if(!AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController),controllerPath))
                throw new InvalidOperationException("controller copy failed");
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            var machine=controller.layers[0].stateMachine;
            var motions=new Dictionary<string,string> {
                ["PV_Idle"]="Idle.fbx",["PV_Walk"]="Walk.fbx",["PV_Run"]="Run.fbx",
                ["PV_CameraIdle"]="IpadIdle.fbx",["PV_CameraRun"]="IpadRun.fbx"
            };
            foreach(var pair in motions) {
                var path="Assets/Art/Characters/LegacyCharacterRoot/Models/AnimationClips/"+pair.Value;
                var clip=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c=>!c.name.StartsWith("__preview__"));
                if(clip==null || !clip.humanMotion)throw new InvalidOperationException("missing humanoid motion: "+path);
                var state=machine.AddState(pair.Key);state.motion=clip;
                if(pair.Key=="PV_CameraIdle")machine.defaultState=state;
            }
            EditorUtility.SetDirty(controller);AssetDatabase.SaveAssetIfDirty(controller);
            animator.runtimeAnimatorController=controller;
            hero.transform.SetPositionAndRotation(new Vector3(-1.8f,.1f,-4),Quaternion.identity);

            // Clone before attaching the prop, so the second performer is unarmed.
            var crew=PresentationMode.CreateActor(hero,new Vector3(1.1f,.1f,-3.4f),Quaternion.identity);
            crew.name="PV_Crew";SceneManager.MoveGameObjectToScene(crew,scene);crew.SetActive(true);
            var crewAnimator=crew.GetComponentInChildren<Animator>(true);
            Pose(animator,"PV_CameraIdle");Pose(crewAnimator,"PV_Idle");
            var hand=animator.GetBoneTransform(HumanBodyBones.RightHand);
            if(hand==null)throw new InvalidOperationException("camera performer has no right hand");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Wieldables/WieldableCamera.prefab");
            var camera=PresentationMode.CreateActor(prefab,Vector3.zero,Quaternion.identity);
            camera.name="PV_FilmingCamera";
            foreach(var a in camera.GetComponentsInChildren<Animator>(true))UnityEngine.Object.DestroyImmediate(a);
            // The production prefab carries multiple historical model choices.
            // Keep the current camera_new mesh and its authored grip helpers.
            foreach(Transform child in camera.transform)
                child.gameObject.SetActive(child.name=="camera_new" || child.name.Contains("Grip") || child.name.Contains("Hint"));
            camera.transform.SetParent(hand,false);camera.transform.localScale=Vector3.one*1.8f;
            camera.transform.rotation=hero.transform.rotation;
            var grip=camera.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="AimRightHandGrip");
            camera.transform.position+=hand.position-grip.position;
            camera.SetActive(true);

            var lamp=GameObject.CreatePrimitive(PrimitiveType.Sphere);lamp.name="PV_RecordLamp";
            UnityEngine.Object.DestroyImmediate(lamp.GetComponent<Collider>());
            lamp.transform.SetParent(camera.transform,false);lamp.transform.localPosition=new Vector3(.22f,.04f,.04f);lamp.transform.localScale=Vector3.one*.025f;
            var material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.name="PV_RecordLamp";
            material.SetColor("_BaseColor",new Color(1,.025f,.01f));material.EnableKeyword("_EMISSION");material.SetColor("_EmissionColor",new Color(4,.03f,.01f));
            AssetDatabase.CreateAsset(material,Root+"/PV_RecordLamp.mat");lamp.GetComponent<Renderer>().sharedMaterial=material;

            var registry=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DirectorRoleRegistry>(true)).Single();
            var so=new SerializedObject(registry);var entries=so.FindProperty("entries");entries.arraySize=3;
            Bind(entries,0,"hero",hero.transform);Bind(entries,1,"crew",crew.transform);Bind(entries,2,"camera",camera.transform);so.ApplyModifiedPropertiesWithoutUndo();

            var adapter=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<CdRebirthAdapter>(true)).Single();
            var settings=new SerializedObject(adapter);var source=(TextAsset)settings.FindProperty("manifestJson").objectReferenceValue;
            var manifest=BridgeJson.Deserialize<CapabilityManifest>(source.text);
            var heroRole=manifest.FindRole("hero");
            manifest.Roles.Add(new RoleDescriptor {Id="crew",Kind="player",DisplayName="Partner",DefaultActor=heroRole.DefaultActor,PresentAtStart=true});
            manifest.Actors.Add(new ActorDescriptor {Id="filming_camera",DisplayName="Physical filming camera"});
            manifest.Roles.Add(new RoleDescriptor {Id="camera",Kind="prop",DisplayName="Physical filming camera",DefaultActor="filming_camera",PresentAtStart=true});
            foreach(var name in motions.Keys)manifest.FindActor(heroRole.DefaultActor).Clips.Add(name);
            var positions=new Dictionary<string,Vector3> {
                ["pv_monster"]=new Vector3(0,.1f,2),["pv_crew_mark"]=new Vector3(1.1f,.1f,-1),
                ["pv_escape_hero"]=new Vector3(-5,.1f,-8),["pv_escape_crew"]=new Vector3(-2.3f,.1f,-8),
                ["pv_monster_chase"]=new Vector3(-1,.1f,-3),
                ["pv_lens"]=new Vector3(-1.6f,1.1f,-2.5f),["pv_front"]=new Vector3(-5,2.2f,-8),
                ["pv_side"]=new Vector3(5,2.1f,-4),["pv_over_shoulder"]=new Vector3(-2.7f,2.2f,-5.7f),
                ["pv_monster_front"]=new Vector3(-2,2.6f,-4),["pv_crew_close"]=new Vector3(2.6f,1.5f,-3),
                ["pv_run_side"]=new Vector3(-7,1.1f,-5),["pv_end_front"]=new Vector3(-7,2.0f,-11),
                ["pv_end_high"]=new Vector3(-7,5.2f,-13)
            };
            var locations=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DirectorLocationRegistry>(true)).Single().transform;
            foreach(var pair in positions) {
                var t=new GameObject(pair.Key).transform;t.SetParent(locations);t.position=pair.Value;
                manifest.Locations.Add(new LocationDescriptor {Id=pair.Key,Space="pv-stage",Position=new[]{pair.Value.x,pair.Value.y,pair.Value.z}});
            }
            File.WriteAllText(Root+"/pv.manifest.json",BridgeJson.Serialize(manifest));AssetDatabase.ImportAsset(Root+"/pv.manifest.json");
            settings.FindProperty("manifestJson").objectReferenceValue=AssetDatabase.LoadAssetAtPath<TextAsset>(Root+"/pv.manifest.json");settings.ApplyModifiedPropertiesWithoutUndo();
            RenderSettings.ambientLight=new Color(.4f,.46f,.54f);RenderSettings.fogDensity=.026f;
            foreach(var l in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Light>(true))) {
                if(l.name=="GD_WarmFill"){l.intensity=12;l.range=30;l.transform.position=new Vector3(-4,4,-5);}
                if(l.name=="GD_MoonRim"){l.intensity=14;l.range=32;l.transform.position=new Vector3(4,5,4);}
            }
            EditorSceneManager.MarkSceneDirty(scene);

        }
        private static void Pose(Animator a,string state){a.enabled=true;a.fireEvents=false;a.applyRootMotion=false;a.cullingMode=AnimatorCullingMode.AlwaysAnimate;a.Rebind();a.Play(state,0,0);a.Update(0);a.speed=0;}
        private static void Bind(SerializedProperty entries,int i,string role,Transform target){var e=entries.GetArrayElementAtIndex(i);e.FindPropertyRelative("roleId").stringValue=role;e.FindPropertyRelative("target").objectReferenceValue=target;}
    }
}
