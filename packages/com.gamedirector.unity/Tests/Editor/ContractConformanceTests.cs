using System;
using System.Collections.Generic;
using System.Linq;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace GameDirector.Unity.Tests {
/// <summary>Characterize the unchanged engine through poses, viewport samples and
/// real take reviews. Expectations come from the Core DLL, not engine kernels.</summary>
public class ContractConformanceTests {
    private static CameraSpec Contract=>EngineContract.Current.Camera;
    private static ConformanceScene Fixture=>Contract.Scene;
    private Scene _scene;
    private GameObject _root;
    private Transform _subject,_visual;
    private CinematicCameraRig _rig;
    private Camera Camera=>_rig.DirectorCamera;
    private static Vector3 V(IReadOnlyList<float> v)=>new Vector3(v[0],v[1],v[2]);
    [SetUp] public void CreateStage() {
        _scene=EditorSceneManager.NewPreviewScene();_root=new GameObject("contract-conformance");SceneManager.MoveGameObjectToScene(_root,_scene);
        _rig=_root.AddComponent<CinematicCameraRig>();
        _subject=new GameObject("hero").transform;_subject.SetParent(_root.transform,false);
        _visual=GameObject.CreatePrimitive(PrimitiveType.Cube).transform;_visual.SetParent(_subject,false);
        _visual.localPosition=V(Fixture.SubjectCenter);_visual.localScale=V(Fixture.SubjectSize);
        Camera.scene=_scene;Camera.aspect=Fixture.Aspect;ResetCamera();
    }
    [TearDown] public void CloseStage() {EditorSceneManager.ClosePreviewScene(_scene);}
    private void ResetCamera() {Camera.transform.SetPositionAndRotation(V(Fixture.From),Quaternion.identity);Camera.fieldOfView=Contract.Lens.DefaultFov;}
    private ShotSpec Shot(string type="lockoff",Dictionary<string,float> parms=null)=>new ShotSpec {
        Type=type,Subject="hero",Frame="full",From="from",To=ShotVocabulary.RequiresTarget(type)==true?"to":null,
        DurationSeconds=Fixture.DurationSeconds,Params=parms
    };
    private void Begin(ShotSpec shot) {
        ResetCamera();_rig.BeginShot(shot,id=>id=="hero"?_subject:null,
            (string id,out Vector3 position,out float heading)=>{position=id=="to"?V(Fixture.To):V(Fixture.From);heading=0;return id=="from"||id=="to";});
    }
    private struct Pose {
        public Vector3 Position;public Quaternion Rotation;public float Fov;
    }
    private Pose Evaluate(ShotSpec shot,double time=.5) {Begin(shot);_rig.Evaluate(time);return new Pose {Position=Camera.transform.position,Rotation=Camera.transform.rotation,Fov=Camera.fieldOfView};}
    private static void Equal(Pose a,Pose b) {Assert.AreEqual(a.Position,b.Position,"position (exact)");Assert.AreEqual(a.Rotation,b.Rotation,"rotation (exact)");Assert.AreEqual(a.Fov,b.Fov,"fov (exact)");}
    private static bool Same(Pose a,Pose b)=>a.Position.Equals(b.Position)&&a.Rotation.Equals(b.Rotation)&&a.Fov.Equals(b.Fov);
    public static IEnumerable<TestCaseData> EaseRows() {
        foreach(var ease in ShotVocabulary.EaseSpecs)foreach(var s in ease.Samples)
            yield return new TestCaseData(ease.Id,s).SetName("Ease_"+ease.Id+"_"+s.P);
    }
    [TestCaseSource(nameof(EaseRows))] public void EaseThroughDollyFraction(string ease,EaseSample s) {
        var shot=Shot("dolly");shot.Ease=ease;Begin(shot);_rig.Evaluate(s.P*Fixture.DurationSeconds);
        Assert.AreEqual(s.E,(Camera.transform.position-V(Fixture.From)).magnitude/(V(Fixture.To)-V(Fixture.From)).magnitude);
    }
    public static IEnumerable<TestCaseData> MotionRows() {
        foreach(var shot in ShotVocabulary.Shots) {
            var i=0;foreach(var s in CameraMotions.Find(shot.Motion).Samples)
                yield return new TestCaseData(shot.Id,s).SetName("Motion_"+shot.Id+"_"+i++ +"_"+s.Observable);
        }
    }
    [TestCaseSource(nameof(MotionRows))] public void MotionThroughBeginAndEvaluate(string type,MotionSample s) {
        var shot=Shot(type,s.Params?.ToDictionary(p=>p.Key,p=>p.Value));shot.Ease=s.Ease;shot.To=s.AuthoredTo?"to":null;
        Begin(shot);var from=V(Fixture.From);var pos0=Camera.transform.position;var right=Camera.transform.right;var target0=CinematicCameraRig.BoundsOf(_subject).center;
        if(s.StepSubject)_subject.position+=V(Fixture.SubjectStep);
        _rig.Evaluate(s.P*Fixture.DurationSeconds);
        var pos=Camera.transform.position;var target=CinematicCameraRig.BoundsOf(_subject).center;
        // No offsets are authored by these rows: the resolved start is the
        // contract anchor, not a possibly broken motion's evaluated p=0 pose.
        var a=from-target0;var b=pos-target;a.y=b.y=0;float actual;
        switch(s.Observable) {
            case MotionObservables.Displacement:actual=(pos-from).magnitude;break;
            case MotionObservables.Fraction:actual=(pos-from).magnitude/(V(Fixture.To)-from).magnitude;break;
            case MotionObservables.Radius:actual=b.magnitude;break;
            case MotionObservables.Radius3D:actual=(pos-target).magnitude;break;
            case MotionObservables.SweptDeg:actual=Vector3.Angle(a,b);break;
            case MotionObservables.Side:actual=Math.Sign(Vector3.Dot(pos-pos0,right));break;
            case MotionObservables.Drift:actual=((pos-target)-(from-target0)).magnitude;break;
            default:throw new ArgumentException(s.Observable);
        }
        Assert.AreEqual(s.Expected,actual,s.Tolerance,type+" "+s.Observable);
    }
    public static IEnumerable<TestCaseData> AimRows() {
        var i=0;foreach(var s in Contract.Aim.Samples)yield return new TestCaseData(s).SetName("Aim_"+i++ +"_pan"+s.Pan+"_tilt"+s.Tilt+"_roll"+s.Roll);
    }
    [TestCaseSource(nameof(AimRows))] public void AimThroughSubjectViewport(ViewSample s) {
        var shot=Shot(parms:new Dictionary<string,float>{{"pan",s.Pan},{"tilt",s.Tilt},{"roll",s.Roll}});shot.Fov=s.Fov;Camera.aspect=s.Aspect;Begin(shot);
        var center=Camera.WorldToViewportPoint(CinematicCameraRig.BoundsOf(_subject).center);
        Assert.Greater(center.z,0);Assert.AreEqual(s.X,center.x,s.Tolerance,"viewport x");Assert.AreEqual(s.Y,center.y,s.Tolerance,"viewport y");
    }
    public static IEnumerable<TestCaseData> LensRows() {
        foreach(var s in Contract.Lens.Samples)yield return new TestCaseData(s).SetName("Lens_"+s.FocalLengthMm);
    }
    [TestCaseSource(nameof(LensRows))] public void LensThroughFocalLengthToFieldOfView(FocalSample s) {
        Assert.AreEqual(s.Fov,UnityEngine.Camera.FocalLengthToFieldOfView(s.FocalLengthMm,Contract.Lens.SensorSizeMm),s.Tolerance);
        var shot=Shot();shot.FocalLengthMm=s.FocalLengthMm;Begin(shot);Assert.AreEqual(s.Fov,Camera.fieldOfView,s.Tolerance);
    }
    public static IEnumerable<TestCaseData> AutoFrameRows() {
        var i=0;foreach(var s in Contract.Rig.Samples)yield return new TestCaseData(s).SetName("AutoFrame_"+i++);
    }
    [TestCaseSource(nameof(AutoFrameRows))] public void AutoFrameThroughRadius3D(AutoFrameSample s) {
        var shot=Shot(parms:s.Coverage.HasValue?new Dictionary<string,float>{{"coverage",s.Coverage.Value}}:null);shot.Frame=s.Frame;shot.From=Contract.CurrentAnchor;Begin(shot);
        Assert.AreEqual(s.Radius3D,(Camera.transform.position-CinematicCameraRig.BoundsOf(_subject).center).magnitude,s.Tolerance);
    }
    public static IEnumerable<TestCaseData> DefaultRows() {
        foreach(var p in ShotVocabulary.Params.Where(p=>p.Default.HasValue))yield return new TestCaseData(p).SetName("Default_"+p.Name);
    }
    private ShotSpec ParamShot(string name,Dictionary<string,float> parms=null) {
        var shot=Shot(name=="orbitDeg"?"orbit":name.StartsWith("toOffset",StringComparison.Ordinal)?"dolly":"lockoff",parms);
        if(name=="coverage")shot.From=Contract.CurrentAnchor;return shot;
    }
    [TestCaseSource(nameof(DefaultRows))] public void OmittedEqualsAuthoredDefaultExactly(ShotParam p) {
        Equal(Evaluate(ParamShot(p.Name)),Evaluate(ParamShot(p.Name,new Dictionary<string,float>{{p.Name,p.Default.Value}})));
    }
    public static IEnumerable<TestCaseData> DerivedRows() {
        foreach(var p in ShotVocabulary.Params.Where(p=>!p.Default.HasValue)) {
            if(p.Derivation==ParamDerivations.FrameCoverage)foreach(var f in ShotVocabulary.Frames)yield return new TestCaseData(p,f.Id).SetName("Derived_"+p.Name+"_"+f.Id);
            else yield return new TestCaseData(p,"full").SetName("Derived_"+p.Name);
        }
    }
    [TestCaseSource(nameof(DerivedRows))] public void OmittedDerivedParamsKeepTheirMeaning(ShotParam p,string frame) {
        if(p.Derivation==ParamDerivations.SubjectBoundsCenter) {
            _visual.localPosition+=new Vector3(1,0,.5f);
            var offset=CinematicCameraRig.BoundsOf(_subject).center-_subject.position;var omitted=Evaluate(Shot());
            Assert.AreEqual(Quaternion.LookRotation(_subject.position+offset-omitted.Position),omitted.Rotation,"omission aims at the full-vector offset, exactly");
            Assert.IsFalse(Same(omitted,Evaluate(Shot(parms:new Dictionary<string,float>{{p.Name,offset.y}}))),"off-center full vector must differ from authored .y");
        } else {
            var shot=ParamShot(p.Name);shot.Frame=frame;
            if(p.Derivation==ParamDerivations.StartFov)shot.Fov=Contract.Lens.Samples[0].Fov;
            var omitted=Evaluate(shot);
            var value=p.Derivation==ParamDerivations.FrameCoverage?(float)ShotVocabulary.Coverage(frame):shot.Fov.Value;
            shot.Params=new Dictionary<string,float>{{p.Name,value}};Equal(omitted,Evaluate(shot));
        }
    }
    public static IEnumerable<TestCaseData> ParamRows() {
        foreach(var p in ShotVocabulary.Params)yield return new TestCaseData(p).SetName("ImplementedParam_"+p.Name);
    }
    private static float NonDefault(string name) {
        switch(name) {case "targetHeight":case "distance":return 2;case "coverage":return 1.5f;case "orbitDeg":return 45;
            case "shake":return 1;case "pan":return 20;case "tilt":return 15;case "roll":return 30;case "endFov":return 70;default:return .5f;}
    }
    [TestCaseSource(nameof(ParamRows))] public void EveryImplementedParamChangesThePose(ShotParam p) {
        var time=p.Name=="shake"?.3:.5;var baseline=Evaluate(ParamShot(p.Name),time);
        var authored=Evaluate(ParamShot(p.Name,new Dictionary<string,float>{{p.Name,NonDefault(p.Name)}}),time);
        Assert.IsFalse(Same(baseline,authored),p.Name+" must affect position, rotation or fov");
    }
    public static IEnumerable<TestCaseData> CadenceRows() {
        var i=0;foreach(var s in ReviewPolicy.Samples)yield return new TestCaseData(s).SetName("Cadence_"+i++);
    }
    private DirectorRuntime Runtime(out StageClockAdapterProbe adapter) {
        adapter=_root.AddComponent<StageClockAdapterProbe>();adapter.BorrowActor=true;adapter.Initialize();
        var registry=new SerializedObject(_root.AddComponent<DirectorRoleRegistry>());var entries=registry.FindProperty("entries");entries.arraySize=1;
        entries.GetArrayElementAtIndex(0).FindPropertyRelative("roleId").stringValue="hero";
        entries.GetArrayElementAtIndex(0).FindPropertyRelative("target").objectReferenceValue=_subject;registry.ApplyModifiedPropertiesWithoutUndo();
        typeof(DirectorRoleRegistry).GetMethod("OnEnable",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(registry.targetObject,null);
        var runtime=_root.AddComponent<DirectorRuntime>();typeof(DirectorRuntime).GetMethod("Awake",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(runtime,null);return runtime;
    }
    [TestCaseSource(nameof(CadenceRows))] public void CadenceThroughRuntimeRenderedAndAdvanceOnlyFrames(CadenceSample s) {
        var runtime=Runtime(out var adapter);
        var timeline=new TimelineAsset {Id="contract-cadence"};var shot=Shot();shot.From=Contract.CurrentAnchor;shot.DurationSeconds=(double)s.FrameCount/s.Fps;
        timeline.Cues.Add(new Cue {Type=CueTypes.CameraShot,Shot=shot});
        try {
            var take=runtime.BeginTake(new TakeRequest {Timeline=timeline,FrameRate=s.Fps,Width=64,Height=64,CaptureFromFrame=s.CaptureFromFrame});Assert.AreEqual(s.FrameCount,take.FrameCount);
            for(var i=0;i<take.FrameCount;i++) {
                var bytes=runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=i,AdvanceOnly=i<s.CaptureFromFrame});
                if(i<s.CaptureFromFrame)Assert.IsNull(bytes);else Assert.IsNotEmpty(bytes);
            }
            Assert.AreEqual(TakeStates.Completed,take.State);
            var dto=JObject.Parse(BridgeJson.Serialize(runtime.GetTakeDto()));var reviews=(JArray)dto["reviews"];
            CollectionAssert.AreEqual(s.Reviewed,reviews.Select(r=>(int)r["frame"]).ToArray());
            foreach(var review in reviews)foreach(var path in ReviewPolicy.Required)Assert.IsNotNull(review.SelectToken(path),path);
        } finally {runtime.Stop();adapter.EndSession();}
    }
    public static IEnumerable<TestCaseData> RequiredRows() {
        foreach(var path in ReviewPolicy.Required)yield return new TestCaseData(path).SetName("ReviewPath_"+path);
    }
    [TestCaseSource(nameof(RequiredRows))] public void CapturedReviewBridgeJsonContainsEveryRequiredPath(string path) {
        var runtime=Runtime(out var adapter);var timeline=new TimelineAsset {Id="contract-review-path"};var shot=Shot();shot.From=Contract.CurrentAnchor;
        timeline.Cues.Add(new Cue {Type=CueTypes.CameraShot,Shot=shot});
        try {
            var take=runtime.BeginTake(new TakeRequest {Timeline=timeline,FrameRate=ReviewPolicy.Samples[0].Fps,Width=64,Height=64});
            Assert.IsNotEmpty(runtime.NextFrame(new FrameRequest {TakeId=take.TakeId,FrameIndex=0}));
            var reviews=(JArray)JObject.Parse(BridgeJson.Serialize(runtime.GetTakeDto()))["reviews"];Assert.AreEqual(1,reviews.Count);
            Assert.IsNotNull(reviews[0].SelectToken(path),path);
        } finally {runtime.Stop();adapter.EndSession();}
    }
}
}
