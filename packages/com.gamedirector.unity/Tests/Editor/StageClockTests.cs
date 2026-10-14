using System;
using GameDirector.Core.Dsl;
using GameDirector.Core.Capture;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
namespace GameDirector.Unity.Tests {
public class StageClockTests {
    private Scene _scene;
    private StageClockAdapterProbe _adapter;
    [SetUp] public void CreateStage() {
        _scene=EditorSceneManager.NewPreviewScene();
        _adapter=Object("director").AddComponent<StageClockAdapterProbe>();_adapter.Initialize();
    }
    [TearDown] public void CloseStage() { try {_adapter.EndSession();} finally {EditorSceneManager.ClosePreviewScene(_scene);} }
    private GameObject Object(string name,Transform parent=null) {
        var go=new GameObject(name);SceneManager.MoveGameObjectToScene(go,_scene);if(parent!=null)go.transform.SetParent(parent,false);return go;
    }
    private Transform Actor() {
        _adapter.BorrowActor=true;var actor=Object("Hero").transform;
        var registry=new SerializedObject(_adapter.gameObject.AddComponent<DirectorRoleRegistry>());
        var entries=registry.FindProperty("entries");entries.arraySize=1;
        entries.GetArrayElementAtIndex(0).FindPropertyRelative("roleId").stringValue="hero";
        entries.GetArrayElementAtIndex(0).FindPropertyRelative("target").objectReferenceValue=actor;registry.ApplyModifiedPropertiesWithoutUndo();
        // Preview scenes do not run Unity callbacks or appear in FindObjectsOfType.
        typeof(DirectorRoleRegistry).GetMethod("OnEnable",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(registry.targetObject,null);
        return actor;
    }
    private ParticleSystem Particle(string name,Transform parent) {
        var p=Object(name,parent).AddComponent<ParticleSystem>();p.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);return p;
    }
    private DirectorRuntime Runtime() {
        var runtime=_adapter.gameObject.AddComponent<DirectorRuntime>();
        typeof(DirectorRuntime).GetMethod("Awake",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(runtime,null);
        return runtime;
    }
    private TimelineAsset Picture(double cameraTime=0) {
        var asset=new TimelineAsset {Id="advance-probe"};
        asset.Cues.Add(new Cue {T=cameraTime,Type=CueTypes.CameraShot,Shot=new ShotSpec {Subject="hero",DurationSeconds=1-cameraTime}});
        return asset;
    }
    [Test] public void AdvanceOnlyNeedsNoActiveShotDoesNotRenderOrInspectAndRetriesDoNotStep() {
        var actor=Actor();var probe=actor.gameObject.AddComponent<ClockedVisualProbe>();var runtime=Runtime();
        var rig=_adapter.GetComponent<CinematicCameraRig>();var renders=0;
        Camera.CameraCallback rendered=c=>{if(c==rig.DirectorCamera)renders++;};Camera.onPreRender+=rendered;
        try {
            var take=runtime.BeginTake(new TakeRequest {Timeline=Picture(.5),FrameRate=4,Width=64,Height=64,CaptureFromFrame=2});
            Assert.AreEqual(4,take.FrameCount);Assert.AreEqual(2,take.CaptureFromFrame);Assert.IsFalse(rig.ShotActive);
            for(int i=0;i<2;i++) {
                var request=new FrameRequest {TakeId=take.TakeId,FrameIndex=i,AdvanceOnly=true};
                Assert.IsNull(runtime.NextFrame(request));var steps=probe.Steps;var time=runtime.Time;
                Assert.IsNull(runtime.NextFrame(request));Assert.AreEqual(steps,probe.Steps);Assert.AreEqual(time,runtime.Time);
            }
            Assert.AreEqual(.5,runtime.Time);Assert.AreEqual(.5,probe.Value);Assert.AreEqual(0,renders);Assert.AreEqual(0,take.CapturedFrames);
            var snapshot=Newtonsoft.Json.Linq.JObject.Parse(BridgeJson.Serialize(runtime.GetTakeDto()));
            Assert.AreEqual(0,((Newtonsoft.Json.Linq.JArray)snapshot["reviews"]).Count);
            Assert.IsTrue(rig.ShotActive);
            runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=2});Assert.Greater(renders,0);Assert.AreEqual(1,take.CapturedFrames);
            runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=3});Assert.AreEqual(TakeStates.Completed,take.State);Assert.AreEqual(2,take.CapturedFrames);
        } finally {Camera.onPreRender-=rendered;runtime.Stop();}
    }
    [TestCase(24,12)] [TestCase(25,13)] [TestCase(30,15)] [TestCase(60,30)]
    public void CapturedPixelsAfterReplayOnlyEqualTheFullPerformance(int fps,int k) {
        var actor=Actor();var visual=GameObject.CreatePrimitive(PrimitiveType.Cube);SceneManager.MoveGameObjectToScene(visual,_scene);visual.transform.SetParent(actor,false);
        var material=new Material(Shader.Find("Unlit/Color"));material.color=Color.red;visual.GetComponent<Renderer>().sharedMaterial=material;
        var probe=actor.gameObject.AddComponent<ClockedVisualProbe>();probe.Visual=visual.transform;
        var camera=_adapter.GetComponent<CinematicCameraRig>().DirectorCamera;camera.scene=_scene;camera.transform.position=new Vector3(0,1,-4);
        var runtime=Runtime();
        try {
            var full=runtime.BeginTake(new TakeRequest {Timeline=Picture(),FrameRate=fps,Width=64,Height=64});byte[] first=null,expected=null;
            for(int i=0;i<=k;i++){var frame=runtime.NextFrame(new FrameRequest {TakeId=full.TakeId,FrameIndex=i});if(i==0)first=frame;if(i==k)expected=frame;}
            runtime.Stop();CollectionAssert.AreNotEqual(first,expected,"fixture must visibly move");
            var cut=runtime.BeginTake(new TakeRequest {Timeline=Picture(),FrameRate=fps,Width=64,Height=64,CaptureFromFrame=k});
            for(int i=0;i<k;i++)Assert.IsNull(runtime.NextFrame(new FrameRequest {TakeId=cut.TakeId,FrameIndex=i,AdvanceOnly=true}));
            var actual=runtime.NextFrame(new FrameRequest {TakeId=cut.TakeId,FrameIndex=k});
            CollectionAssert.AreEqual(expected,actual);Assert.AreEqual(1,cut.CapturedFrames);
        } finally {runtime.Stop();UnityEngine.Object.DestroyImmediate(material);}
    }
    [Test] public void FrameModeAndOrderRefuseBeforeMutatingAndLastCaptureRetryIsIdentical() {
        Actor();var runtime=Runtime();
        try {
            var take=runtime.BeginTake(new TakeRequest {Timeline=Picture(),FrameRate=4,Width=64,Height=64,CaptureFromFrame=1});
            Assert.Throws<BridgeRequestException>(()=>runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=0}));
            Assert.Throws<BridgeRequestException>(()=>runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=1,AdvanceOnly=true}));Assert.AreEqual(0,runtime.Time);
            runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=0,AdvanceOnly=true});
            var request=new FrameRequest {TakeId=take.TakeId,FrameIndex=1};var bytes=runtime.NextFrame(request);var time=runtime.Time;
            CollectionAssert.AreEqual(bytes,runtime.NextFrame(request));Assert.AreEqual(time,runtime.Time);Assert.AreEqual(1,take.CapturedFrames);
            Assert.Throws<BridgeRequestException>(()=>runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=0,AdvanceOnly=true}));
            Assert.AreEqual(TakeStates.Capturing,take.State);
        } finally {runtime.Stop();}
    }
    [TestCase("lockoff")] [TestCase("orbit")]
    public void AuthoredCameraToMustBindBeforeAnyStageMutation(string kind) {
        var actor=Actor();var visual=actor.gameObject.AddComponent<ClockedVisualProbe>();var position=actor.position;
        _adapter.Manifest.Locations.Add(new LocationDescriptor {Id="missing-target"});
        var asset=Picture();asset.Cues[0].Shot.Type=kind;asset.Cues[0].Shot.To="missing-target";
        var ex=Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(asset));Assert.AreSame(BridgeErrors.StateConflict,ex.Error);
        StringAssert.Contains("unbound camera to: missing-target",ex.Message);
        Assert.AreEqual(position,actor.position);Assert.AreEqual(7,visual.Value);Assert.IsTrue(visual.enabled);Assert.IsFalse(_adapter.GetComponent<CinematicCameraRig>().ShotActive);
    }
    [Test] public void OptionalVideoAndLegacyAnimationClocksAreRefusedWithTheirPaths() {
        var stage=Object("Stage").transform;
        var type=System.Type.GetType("UnityEngine.Video.VideoPlayer, UnityEngine.VideoModule");Assert.IsNotNull(type);
        var video=Object("screen",stage).AddComponent(type);type.GetProperty("playOnAwake").SetValue(video,true);
        var animation=Object("legacy",stage).AddComponent<Animation>();animation.playAutomatically=true;
        var ex=Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset()));Assert.AreSame(BridgeErrors.StateConflict,ex.Error);
        StringAssert.Contains("Stage/screen (VideoPlayer)",ex.Message);StringAssert.Contains("Stage/legacy (Animation)",ex.Message);
        ((Behaviour)video).enabled=false;animation.enabled=false;Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        ((Behaviour)video).enabled=true;_adapter.Exempt=video;Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        type.GetProperty("playOnAwake").SetValue(video,false);animation.enabled=true;_adapter.Exempt=animation;
        Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        animation.playAutomatically=false;_adapter.Exempt=null;Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
    }
    [Test] public void AdapterCanAddAnAutonomousScriptClockWithoutChangingTheBaseAudit() {
        var custom=Object("Stage").AddComponent<AutonomousClockProbe>();
        var ex=Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset()));Assert.AreSame(BridgeErrors.StateConflict,ex.Error);
        StringAssert.Contains("Stage (AutonomousClockProbe)",ex.Message);
        custom.enabled=false;Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        custom.enabled=true;_adapter.Exempt=custom;Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
    }
    [Test] public void StrayAnimatorAndPlayOnAwakeParticlesRefuseWithFullPathsBeforeLeasing() {
        var stage=Object("Stage").transform;var a=Object("stray",stage).AddComponent<Animator>();var p=Particle("smoke",stage);
        var ex=Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset()));
        StringAssert.Contains("stage has 2 clocks the director does not own:",ex.Message);
        StringAssert.Contains("Stage/stray (Animator)",ex.Message);StringAssert.Contains("Stage/smoke (ParticleSystem)",ex.Message);
        StringAssert.EndsWith("; disable them in the stage, make them presentation participants, or exempt them in the adapter",ex.Message);
        Assert.IsTrue(a.enabled);Assert.IsTrue(p.useAutoRandomSeed);
        a.enabled=false;var main=p.main;main.playOnAwake=false;
        Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        // Inactive objects are not a clock-ownership declaration; they may wake later.
        a.enabled=true;a.gameObject.SetActive(false);
        StringAssert.Contains("Stage/stray (Animator)",Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset())).Message);
    }
    [Test] public void ClockRefusalNamesThePathEvenIfItsAutonomousMotionMadeTheFingerprintStale() {
        Actor();var stage=Object("Stage").transform;Object("stray",stage).AddComponent<Animator>();
        var runtime=_adapter.gameObject.AddComponent<DirectorRuntime>();
        typeof(DirectorRuntime).GetMethod("Awake",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(runtime,null);
        var asset=new TimelineAsset {Id="clock-refusal"};
        asset.Cues.Add(new Cue {Type=CueTypes.CameraShot,Shot=new ShotSpec {Subject="hero",DurationSeconds=1}});
        var ex=Assert.Throws<BridgeRequestException>(()=>runtime.BeginTake(new TakeRequest {Timeline=asset,FrameRate=1,Width=64,Height=64,SourceFingerprint="stale"}));
        StringAssert.Contains("Stage/stray (Animator)",ex.Message);Assert.IsFalse(runtime.IsCapturing);
    }
    [Test] public void PlayingParticlesAndPlayOnAwakeDirectorsAreAlsoRefused() {
        var stage=Object("Stage").transform;var p=Particle("sparks",stage);var main=p.main;main.playOnAwake=false;p.Play(false);
        var d=Object("timeline",stage).AddComponent<PlayableDirector>();d.playOnAwake=true;
        var ex=Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset()));
        StringAssert.Contains("Stage/sparks (ParticleSystem)",ex.Message);StringAssert.Contains("Stage/timeline (PlayableDirector)",ex.Message);
        p.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);d.playOnAwake=false;
        Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
    }
    [Test] public void BorrowedRoleAndOnlyTheRigsOwnedCameraAreExempt() {
        var actor=Actor();Object("animation",actor).AddComponent<Animator>();Particle("smoke",actor);
        _adapter.Camera.gameObject.AddComponent<Animator>();
        Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        var unrelated=Object("other-camera",_adapter.transform);unrelated.AddComponent<Animator>();
        StringAssert.Contains("director/other-camera (Animator)",Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset())).Message);
    }
    [Test] public void AdapterExemptionIsExplicitAndComponentSpecific() {
        var root=Object("Stage");var a=root.AddComponent<Animator>();var p=root.AddComponent<ParticleSystem>();
        _adapter.Exempt=p;a.enabled=false;Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        a.enabled=true;_adapter.Exempt=a;
        StringAssert.Contains("Stage (ParticleSystem)",Assert.Throws<BridgeRequestException>(()=>_adapter.Preflight(new TimelineAsset())).Message);
        p.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);var main=p.main;main.playOnAwake=false;
        Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
    }
    [Test] public void StageParticipantsOutsideRolesAreLeasedClockedAndRestored() {
        var root=Object("Environment");var visual=root.AddComponent<ClockedVisualProbe>();var p=Particle("smoke",root.transform);
        var nested=Object("nested",root.transform).AddComponent<ClockedVisualProbe>();
        Assert.DoesNotThrow(()=>_adapter.Preflight(new TimelineAsset()));
        _adapter.BeginSession();Assert.IsFalse(visual.enabled);Assert.AreEqual(0,visual.Value);Assert.AreEqual(1,nested.Steps);
        _adapter.AdvancePresentation(.25,.25);Assert.AreEqual(.25,visual.Value);Assert.IsFalse(p.useAutoRandomSeed);Assert.AreEqual(2,nested.Steps);
        _adapter.EndSession();Assert.IsTrue(visual.enabled);Assert.AreEqual(7,visual.Value);Assert.IsTrue(p.useAutoRandomSeed);Assert.AreEqual(7,nested.Value);
    }
    [Test] public void LeasedParticlesKeepTheExplicitAuthorSeedAndRestoreIt() {
        var p=Particle("smoke",Actor());p.randomSeed=8675309;p.useAutoRandomSeed=false;
        _adapter.Preflight(new TimelineAsset());_adapter.BeginSession();
        Assert.IsFalse(p.useAutoRandomSeed);Assert.AreEqual(8675309u,p.randomSeed);
        _adapter.AdvancePresentation(.25,.25);Assert.AreEqual(8675309u,p.randomSeed);
        _adapter.EndSession();Assert.IsFalse(p.useAutoRandomSeed);Assert.AreEqual(8675309u,p.randomSeed);
    }
    [Test] public void AutomaticSeedsDifferByPathRepeatAcrossTakesAndRestoreAutoMode() {
        var actor=Actor();var a=Particle("left",actor);var b=Particle("right",actor);
        _adapter.BeginSession();var left=a.randomSeed;var right=b.randomSeed;
        Assert.AreNotEqual(left,right);Assert.AreNotEqual(0u,left);Assert.AreNotEqual(0u,right);
        _adapter.EndSession();Assert.IsTrue(a.useAutoRandomSeed);Assert.IsTrue(b.useAutoRandomSeed);
        _adapter.BeginSession();Assert.AreEqual(left,a.randomSeed);Assert.AreEqual(right,b.randomSeed);
    }
}
}
