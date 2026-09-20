using System;
using System.Collections.Generic;
using System.Linq;
using GameDirector.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.CDREBIRTH.Editor
{
    /// <summary>Creates a separate, explicitly authored film stage. Never overwrites
    /// an existing scene or saves an unrelated dirty scene.</summary>
    public static class DirectorStageBuilder
    {
        public static string Build(string source,string destination)
        {
            if(EditorApplication.isPlaying || EditorApplication.isCompiling)throw new InvalidOperationException("Editor must be idle");
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("unsaved scene; preserve it before building a stage");
            if(!source.StartsWith("Assets/Scenes/GameDirector/",StringComparison.Ordinal) || !destination.StartsWith("Assets/Scenes/GameDirector/",StringComparison.Ordinal) || !destination.EndsWith(".unity",StringComparison.Ordinal))
                throw new ArgumentException("stage must be inside the director sandbox folder");
            if(AssetDatabase.LoadAssetAtPath<SceneAsset>(destination)!=null)throw new InvalidOperationException("destination already exists");
            if(!AssetDatabase.CopyAsset(source,destination))throw new InvalidOperationException("could not copy source stage");
            var scene=EditorSceneManager.OpenScene(destination,OpenSceneMode.Single);
            foreach(var root in scene.GetRootGameObjects()) {
                foreach(var c in root.GetComponentsInChildren<DirectorLocationRegistry>(true))UnityEngine.Object.DestroyImmediate(c);
                foreach(var c in root.GetComponentsInChildren<DirectorRoleRegistry>(true))UnityEngine.Object.DestroyImmediate(c);
                // The old scene accidentally saved three spawned sanctuary hubs.
                // They are set dressing in this COPY, not authored level truth.
                if(root.name=="SanctuaryWorldRoot" || root.name=="GD_Locations")UnityEngine.Object.DestroyImmediate(root);
            }
            var hero=scene.GetRootGameObjects().Single(x=>x.name=="GD_Hero");
            hero.transform.SetPositionAndRotation(new Vector3(-2,0.1f,-2),Quaternion.Euler(0,155,0));
            var rr=new GameObject("GD_Roles").AddComponent<DirectorRoleRegistry>();
            var roles=new SerializedObject(rr);var entries=roles.FindProperty("entries");entries.arraySize=1;
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("roleId").stringValue="hero";
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("target").objectReferenceValue=hero.transform;roles.ApplyModifiedPropertiesWithoutUndo();
            var locations=new GameObject("GD_Locations").AddComponent<DirectorLocationRegistry>();
            var points=new Dictionary<string,Vector3>{
                ["gf_gate"]=new Vector3(0,.1f,0),["gf_stage"]=new Vector3(3,.1f,0),
                ["gf_cam_north"]=new Vector3(0,3,-12),["gf_cam_low"]=new Vector3(0,1.5f,-9),["gf_cam_orbit"]=new Vector3(6,2,-7),
                ["sc_north_gate"]=new Vector3(0,2,-10),["sc_west_gate"]=new Vector3(-7,2,-4),["sc_east_gate"]=new Vector3(7,2,-4),
                ["sc_high_gate"]=new Vector3(0,10,-8),["sc_north_stage"]=new Vector3(3,2,-9),["sc_east_stage"]=new Vector3(8,2.5f,-2)
            };
            foreach(var point in points){var t=new GameObject(point.Key).transform;t.SetParent(locations.transform);t.position=point.Value;t.rotation=Quaternion.Euler(0,180,0);}
            foreach(var light in UnityEngine.Object.FindObjectsOfType<Light>()) {
                if(light.type==LightType.Directional){light.intensity=1.5f;light.color=new Color(1,.83f,.68f);light.transform.rotation=Quaternion.Euler(35,-20,0);light.shadowStrength=.7f;}
            }
            AddLight("GD_MoonRim",new Vector3(4,6,4),new Color(.25f,.48f,1),8,30);
            AddLight("GD_WarmFill",new Vector3(-4,3,-6),new Color(1,.55f,.22f),8,24);
            var fill=new GameObject("GD_BlueFill").AddComponent<Light>();fill.type=LightType.Directional;fill.intensity=.42f;fill.color=new Color(.35f,.48f,.75f);fill.shadows=LightShadows.None;fill.transform.rotation=Quaternion.Euler(20,155,0);
            RenderSettings.skybox=null;
            RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight=new Color(.3f,.35f,.44f);
            RenderSettings.fog=true;RenderSettings.fogMode=FogMode.ExponentialSquared;RenderSettings.fogDensity=.035f;RenderSettings.fogColor=new Color(.035f,.045f,.07f);
            var director=scene.GetRootGameObjects().Single(x=>x.name=="GameDirector");
            if(director.GetComponent<OfflinePresentationScene>()==null)director.AddComponent<OfflinePresentationScene>();
            if(director.GetComponent<DirectorRuntime>()==null)director.AddComponent<DirectorRuntime>();
            EditorSceneManager.MarkSceneDirty(scene);
            if(!EditorSceneManager.SaveScene(scene,destination))throw new InvalidOperationException("stage save failed");
            return destination;
        }
        private static void AddLight(string name,Vector3 p,Color color,float intensity,float range){var l=new GameObject(name).AddComponent<Light>();l.type=LightType.Point;l.transform.position=p;l.color=color;l.intensity=intensity;l.range=range;}
    }
}
