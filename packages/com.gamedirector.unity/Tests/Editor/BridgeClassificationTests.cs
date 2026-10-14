using System;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace GameDirector.Unity.Tests {
// One assertion per classification site: what blames the caller
// (invalid_request) and what blames the stage/state (state_conflict). Flipping
// any site to the wrong class must fail here.
public class BridgeClassificationTests {
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
        typeof(DirectorRoleRegistry).GetMethod("OnEnable",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(registry.targetObject,null);
        return actor;
    }
    private DirectorRuntime Runtime() {
        var runtime=_adapter.gameObject.AddComponent<DirectorRuntime>();
        typeof(DirectorRuntime).GetMethod("Awake",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(runtime,null);
        return runtime;
    }
    private static TimelineAsset Picture(double duration=1) {
        var asset=new TimelineAsset {Id="classification-probe"};
        asset.Cues.Add(new Cue {T=0,Type=CueTypes.CameraShot,Shot=new ShotSpec {Subject="hero",DurationSeconds=duration}});
        return asset;
    }
    private static void ClassifiedAs(BridgeErrorDefinition error,TestDelegate action) {
        var ex=Assert.Throws<BridgeRequestException>(action);Assert.AreSame(error,ex.Error);
    }
    [Test] public void TakeStateRefusalsAreStateConflicts() {
        Actor();var runtime=Runtime();
        try {
            var take=runtime.BeginTake(new TakeRequest{Timeline=Picture(),FrameRate=4,Width=64,Height=64});
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.BeginTake(new TakeRequest{Timeline=Picture(),FrameRate=4,Width=64,Height=64}));
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.NextFrame(new FrameRequest{TakeId="not-owned",FrameIndex=0}));
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.NextFrame(new FrameRequest{TakeId=take.TakeId,FrameIndex=9}));
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.NextFrame(new FrameRequest{TakeId=take.TakeId,FrameIndex=0,AdvanceOnly=true}));
            runtime.NextFrame(new FrameRequest{TakeId=take.TakeId,FrameIndex=0});
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.NextFrame(new FrameRequest{TakeId=take.TakeId,FrameIndex=2}));
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.StopTake(new TakeStopRequest{TakeId="not-owned"}));
        } finally {runtime.Stop();}
    }
    [Test] public void StaleFingerprintIsAStateConflict() {
        Actor();var runtime=Runtime();
        try { ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.BeginTake(new TakeRequest{Timeline=Picture(),FrameRate=4,Width=64,Height=64,SourceFingerprint="stale"})); }
        finally {runtime.Stop();}
    }
    [Test] public void ParseCompileAndAdmissionRefusalsAreInvalidInput() {
        Actor();var runtime=Runtime();
        try {
            ClassifiedAs(BridgeErrors.InvalidRequest,()=>runtime.PlayFromJson("{broken"));
            var ghost=new TimelineAsset{Id="ghost"};
            ghost.Cues.Add(new Cue{Type=CueTypes.CameraShot,Shot=new ShotSpec{Subject="ghost",DurationSeconds=1}});
            ClassifiedAs(BridgeErrors.InvalidRequest,()=>runtime.PlayFromJson(BridgeJson.Serialize(ghost)));
            ClassifiedAs(BridgeErrors.InvalidRequest,()=>runtime.BeginTake(new TakeRequest{Timeline=Picture(),FrameRate=4,Width=3,Height=64}));
            ClassifiedAs(BridgeErrors.InvalidRequest,()=>runtime.BeginTake(null));
            ClassifiedAs(BridgeErrors.InvalidRequest,()=>runtime.NextFrame(null));
            ClassifiedAs(BridgeErrors.InvalidRequest,()=>runtime.StopTake(null));
        } finally {runtime.Stop();}
    }
    [TestCase("version")] [TestCase("duplicate")] [TestCase("capability")]
    public void ParsedInvalidEngineManifestIsInternalForBothTakeAndPlay(string kind) {
        Actor();var manifest=_adapter.Manifest;var runtime=Runtime();
        if(kind=="version")manifest.ManifestVersion="0.2";
        if(kind=="duplicate")manifest.Roles.Add(manifest.Roles[0]);
        if(kind=="capability")manifest.Capabilities[CapabilityKeys.AnimationFade]="wrong";
        try {
            var take=Assert.Throws<InvalidOperationException>(()=>runtime.BeginTake(new TakeRequest{Timeline=Picture(),FrameRate=4,Width=64,Height=64}));
            Assert.AreSame(BridgeErrors.Internal,BridgeRequestException.Classify(take));StringAssert.Contains("manifest",take.Message);
            var play=Assert.Throws<InvalidOperationException>(()=>runtime.PlayFromJson(BridgeJson.Serialize(Picture())));
            Assert.AreSame(BridgeErrors.Internal,BridgeRequestException.Classify(play));StringAssert.Contains("manifest",play.Message);
        } finally {runtime.Stop();}
    }
    [Test] public void MissingRoleDuringRuntimeExecutionIsInternalNotAPreflightRefusal() {
        var ex=Assert.Throws<InvalidOperationException>(()=>_adapter.PlayAnimation("missing","wave",0));
        Assert.AreSame(BridgeErrors.Internal,BridgeRequestException.Classify(ex));
    }
    private void DuplicateRole() {
        var registry=Object("other-roles").AddComponent<DirectorRoleRegistry>();
        var serialized=new SerializedObject(registry);var entries=serialized.FindProperty("entries");entries.arraySize=1;
        entries.GetArrayElementAtIndex(0).FindPropertyRelative("roleId").stringValue="hero";
        entries.GetArrayElementAtIndex(0).FindPropertyRelative("target").objectReferenceValue=Object("OtherHero").transform;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        typeof(DirectorRoleRegistry).GetMethod("OnEnable",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(registry,null);
    }
    [Test] public void AmbiguousAnimationDiscoveryDefersToPreflightNotCallerAdmission() {
        Actor();_adapter.ProposeAnimation=true;DuplicateRole();var runtime=Runtime();
        try {
            Assert.DoesNotThrow(()=>{var manifest=_adapter.Manifest;});
            CollectionAssert.Contains(_adapter.Manifest.Actors[0].Clips,"proposed");
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.BeginTake(new TakeRequest{Timeline=Picture(),FrameRate=4,Width=64,Height=64}));
            ClassifiedAs(BridgeErrors.StateConflict,()=>runtime.PlayFromJson(BridgeJson.Serialize(Picture())));
        } finally {runtime.Stop();}
    }
    [TestCase("role")] [TestCase("location")]
    public void DuplicateStageBindingsAreStateConflictsOnlyInPreflight(string kind) {
        Actor();var manifest=_adapter.Manifest;var picture=Picture();
        if(kind=="role")DuplicateRole();
        else {
            foreach(var name in new[]{"locations-a","locations-b"}) {
                var registry=Object(name).AddComponent<DirectorLocationRegistry>();Object("gf_stage",registry.transform);
                typeof(DirectorLocationRegistry).GetMethod("OnEnable",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(registry,null);
            }
            manifest.Locations.Add(new LocationDescriptor{Id="gf_stage"});picture.Cues[0].Shot.From="gf_stage";
        }
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(picture));
        // The same resolver fault during execution is never a stage refusal.
        var runtime=Assert.Catch<InvalidOperationException>(()=> {
            if(kind=="role")_adapter.PlayAnimation("hero","missing",0);
            else _adapter.FaceRole("hero","gf_stage");
        });
        Assert.AreSame(BridgeErrors.Internal,BridgeRequestException.Classify(runtime));
    }
    [Test] public void EveryPreflightRefusalIsAStateConflict() {
        var manifest=_adapter.Manifest;
        manifest.Capabilities.Remove(CapabilityKeys.DirectorMode); // undeclared mode
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(new TimelineAsset()));
        manifest.Capabilities[CapabilityKeys.DirectorMode]=DirectorModes.OfflineSandbox;
        manifest.Roles.Add(new RoleDescriptor{Id="ghost",PresentAtStart=true}); // unbound present-at-start role
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(new TimelineAsset()));
        manifest.Roles.RemoveAt(manifest.Roles.Count-1);
        var stray=Object("stray").AddComponent<Animator>(); // unowned stage clock
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(new TimelineAsset()));
        UnityEngine.Object.DestroyImmediate(stray);
        var location=new TimelineAsset{Id="location"};
        location.Cues.Add(new Cue{T=0,Type=CueTypes.CameraShot,Shot=new ShotSpec{Subject="hero",From="nowhere",DurationSeconds=1}});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(location));
        location.Cues[0].Shot.From=ShotVocabulary.CurrentAnchor;location.Cues[0].Shot.To="nowhere";
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(location));
        manifest.Locations.Add(new LocationDescriptor{Id="nowhere"});
        var heading=new TimelineAsset{Id="heading"};heading.Cues.Add(new Cue{Type=CueTypes.ActorFace,Role="hero",HeadingTo="nowhere"});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(heading));
        var move=new TimelineAsset{Id="move"};move.Cues.Add(new Cue{Type=CueTypes.ActorMove,Role="hero",To="nowhere"});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(move));
        var spawn=new TimelineAsset{Id="spawn"};spawn.Cues.Add(new Cue{Type=CueTypes.ActorSpawn,Role="hero",Location="nowhere"});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(spawn));
        spawn.Cues[0].Location="bound";manifest.Locations.Add(new LocationDescriptor{Id="bound",Position=new[]{0f,0f,0f}});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(spawn));
        var anim=new TimelineAsset{Id="anim"};anim.Cues.Add(new Cue{T=0,Type=CueTypes.ActorAnim,Role="hero",Clip="missing"});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(anim));
        var audio=new TimelineAsset{Id="audio"};audio.Cues.Add(new Cue{T=0,Type=CueTypes.AudioPlay,AudioId="missing"});
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(audio));
        audio.Cues[0].Type=CueTypes.AudioStop;
        ClassifiedAs(BridgeErrors.StateConflict,()=>_adapter.Preflight(audio));
        var ex=Assert.Throws<BridgeRequestException>(()=>{throw _adapter.Refuse("adapter stage refusal");});
        Assert.AreSame(BridgeErrors.StateConflict,ex.Error);
    }
}
}
