using GameDirector.Unity;
using NUnit.Framework;
using UnityEngine;
namespace GameDirector.Unity.Tests {
public class PresentationParticipantTests {
    [Test] public void ScriptVisualsAreClockedAndRestoredWhileGameplayStaysDisabled() {
        var root=new GameObject("participant-test");
        try {
            var visual=root.AddComponent<ClockedVisualProbe>();var gameplay=root.AddComponent<GameplayProbe>();
            using(var lease=PresentationMode.Acquire(root)) {
                Assert.IsFalse(visual.enabled);Assert.IsFalse(gameplay.enabled);
                lease.Prepare();lease.Evaluate(DirectorPresentationPhase.BeforeAnimation,.5,.5);Assert.AreEqual(0,visual.Steps);
                lease.Evaluate(DirectorPresentationPhase.AfterAnimation,.5,.5);Assert.AreEqual(.5,visual.Value);Assert.AreEqual(1,visual.Steps);
            }
            Assert.AreEqual(7,visual.Value);Assert.IsTrue(visual.enabled);Assert.IsTrue(gameplay.enabled);
        }finally{Object.DestroyImmediate(root);}
    }
    [Test] public void FailedPrepareAndRestoreStillReleaseEveryBorrowedComponent() {
        var root=new GameObject("participant-failure");
        try {
            var visual=root.AddComponent<ClockedVisualProbe>();var gameplay=root.AddComponent<GameplayProbe>();
            visual.FailPrepare=true;var lease=PresentationMode.Acquire(root);
            Assert.Throws<System.InvalidOperationException>(()=>lease.Prepare());
            visual.FailRestore=true;Assert.Throws<System.InvalidOperationException>(()=>lease.Dispose());
            Assert.AreEqual(7,visual.Value);Assert.IsTrue(gameplay.enabled);Assert.IsTrue(visual.enabled);
            lease.Dispose();
        }finally{Object.DestroyImmediate(root);}
    }
    [Test] public void LiveSourceFingerprintChangesWithUnsavedVisualInputs() {
        var scene=UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try {
            var owner=new GameObject("director");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner,scene);
            var adapter=owner.AddComponent<GenericSceneAdapter>();
            typeof(GameDirectorAdapterBase).GetMethod("Awake",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(adapter,null);
            var actor=new GameObject("actor");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(actor,scene);
            var visual=actor.AddComponent<ClockedVisualProbe>();var light=actor.AddComponent<Light>();
            var fingerprint=typeof(GameDirectorAdapterBase).GetMethod("SourceFingerprint",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            string Read() => (string)fingerprint.Invoke(adapter,null);
            var initial=Read();var restore=owner.GetComponent<CinematicCameraRig>().CaptureState();restore();Assert.AreEqual(initial,Read());
            var first=Read();light.intensity+=1;var second=Read();Assert.AreNotEqual(first,second);
            visual.Value+=1;var third=Read();Assert.AreNotEqual(second,third);
            var child=new GameObject("prop");child.transform.SetParent(actor.transform,false);
            var fourth=Read();child.transform.localPosition=Vector3.right;Assert.AreNotEqual(fourth,Read());
            var fifth=Read();child.SetActive(false);Assert.AreNotEqual(fifth,Read());
            var sixth=Read();actor.layer=2;Assert.AreNotEqual(sixth,Read());
            actor.transform.SetParent(owner.transform,false);var seventh=Read();visual.Value+=1;Assert.AreNotEqual(seventh,Read());
        } finally {UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);}
    }
    [Test] public void InactiveClonesKeepOnlyDeclaredVisualParticipants() {
        var prefab=new GameObject("participant-source");GameObject clone=null;
        try {
            prefab.SetActive(false);prefab.AddComponent<ClockedVisualProbe>();prefab.AddComponent<GameplayProbe>();prefab.AddComponent<Rigidbody>();
            clone=PresentationMode.CreateActor(prefab,Vector3.zero,Quaternion.identity);
            Assert.IsFalse(clone.activeSelf);Assert.IsNotNull(clone.GetComponent<ClockedVisualProbe>());Assert.IsNull(clone.GetComponent<GameplayProbe>());
            Assert.IsFalse(clone.GetComponent<ClockedVisualProbe>().enabled);Assert.IsTrue(clone.GetComponent<Rigidbody>().isKinematic);
            Assert.IsNotNull(prefab.GetComponent<GameplayProbe>());
        }finally{if(clone!=null)Object.DestroyImmediate(clone);Object.DestroyImmediate(prefab);}
    }
    [Test] public void SpawnedParticipantsPrepareAndRestoreBeforeDestruction() {
        var owner=new GameObject("spawn-owner");var prefab=new GameObject("spawn-source");
        prefab.SetActive(false);prefab.AddComponent<ClockedVisualProbe>();
        var adapter=owner.AddComponent<SpawnParticipantAdapterProbe>();adapter.Prefab=prefab;adapter.Initialize();ClockedVisualProbe.Restores=0;
        try {
            adapter.BeginSession();adapter.SpawnRole("spawned","origin",null);
            var visual=adapter.Actor.GetComponent<ClockedVisualProbe>();
            Assert.AreEqual(0,visual.Value);Assert.AreEqual(1,visual.Steps);
            adapter.PlayAnimation("spawned","wave",0);Assert.AreEqual("wave",visual.Pose);
            adapter.AdvancePresentation(.25,.25);Assert.AreEqual(.25,visual.Value);
            adapter.DespawnRole("spawned");Assert.AreEqual(1,ClockedVisualProbe.Restores);
            Assert.IsTrue(visual==null);adapter.AdvancePresentation(.25,.5);
            Assert.DoesNotThrow(()=>adapter.EndSession());
        } finally {adapter.EndSession();Object.DestroyImmediate(owner);Object.DestroyImmediate(prefab);}
    }
    [Test] public void DeclaredProceduralAnimationDoesNotRequireAnimator() {
        var owner=new GameObject("procedural-owner");var prefab=new GameObject("procedural-source");
        prefab.SetActive(false);prefab.AddComponent<ClockedVisualProbe>();
        var adapter=owner.AddComponent<SpawnParticipantAdapterProbe>();adapter.Prefab=prefab;adapter.Initialize();
        try {
            CollectionAssert.Contains(adapter.Manifest.FindActor("scripted").Clips,"wave");
            var timeline=new GameDirector.Core.Dsl.TimelineAsset();
            timeline.Cues.Add(new GameDirector.Core.Dsl.Cue{Type="actor.spawn",Role="spawned",Location="origin"});
            timeline.Cues.Add(new GameDirector.Core.Dsl.Cue{Type="actor.anim",Role="spawned",Clip="wave"});
            Assert.DoesNotThrow(()=>adapter.Preflight(timeline));
            Assert.IsNull(prefab.GetComponent<Animator>());
        } finally {Object.DestroyImmediate(owner);Object.DestroyImmediate(prefab);}
    }
}

}
