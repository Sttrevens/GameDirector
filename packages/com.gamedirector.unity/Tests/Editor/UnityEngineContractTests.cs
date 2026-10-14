using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameDirector.Core.Dsl;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
namespace GameDirector.Unity.Tests {
public class UnityEngineContractTests {
    private const BindingFlags PrivateStatic=BindingFlags.NonPublic|BindingFlags.Static,PrivateInstance=BindingFlags.NonPublic|BindingFlags.Instance;
    private static CameraSpec Camera=>EngineContract.Current.Camera;
    private static IReadOnlyList<string> ImplementedParams=>UnityEngineContract.ImplementedParams;
    private static ContractVersion Version(int major,int minor)=>(ContractVersion)Activator.CreateInstance(typeof(ContractVersion),BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{major,minor},null);
    private static string Check(ContractVersion version=null,IEnumerable<string> motions=null,IEnumerable<string> eases=null,IEnumerable<string> formulas=null,IEnumerable<string> parameters=null,IEnumerable<string> implemented=null,int major=1,int minor=0)=>
        UnityEngineContract.Check(version??EngineContract.Current.Version,motions??ShotVocabulary.Shots.Select(s=>s.Motion),eases??ShotVocabulary.EaseSpecs.Select(e=>e.Formula),
            formulas??new[]{Camera.Progress.Id,Camera.Lens.FocalToFov.Id,Camera.Rig.AutoFrameDistance.Id},parameters??ShotVocabulary.Params.Select(p=>p.Name),implemented??ImplementedParams,major,minor);
    private static float Ease(float p,string formula)=>(float)typeof(CinematicCameraRig).GetMethod("Ease",PrivateStatic).Invoke(null,new object[]{p,formula});
    private Scene _scene;
    private GameObject _root;
    private CinematicCameraRig _rig;
    [SetUp] public void CreateStage() {_scene=EditorSceneManager.NewPreviewScene();_root=new GameObject("unity-engine-contract");SceneManager.MoveGameObjectToScene(_root,_scene);_rig=_root.AddComponent<CinematicCameraRig>();}
    [TearDown] public void CloseStage() {EditorSceneManager.ClosePreviewScene(_scene);}
    [Test] public void ShippedDllIsAcceptedWithoutTypeInitializationFailure() {Assert.IsNull(UnityEngineContract.Refusal());Assert.DoesNotThrow(()=>UnityEngineContract.Require());}
    [Test] public void ImplementedParamsExactlyCoverTheBuiltinTable() {CollectionAssert.AreEquivalent(ShotVocabulary.Params.Select(p=>p.Name),ImplementedParams);Assert.AreEqual(15,ImplementedParams.Count);}
    [Test] public void DifferentMajorRefusesAndNamesBothVersions() {var why=Check(version:Version(2,0));Assert.IsNotNull(why);StringAssert.Contains("2.0",why);StringAssert.Contains("1.0",why);}
    [Test] public void HigherRequiredEngineMinorRefuses() {var why=Check(version:Version(1,1),minor:2);Assert.IsNotNull(why);StringAssert.Contains("1.2",why);StringAssert.Contains("1.1",why);}
    [Test] public void LowerRequiredEngineMinorIsAccepted() {Assert.IsNull(Check(version:Version(1,1),minor:0),"Core provides 1.1, which includes the engine's required 1.0 floor");}
    [Test] public void UncoveredMotionNamesItsId() {var why=Check(motions:new[]{"uncovered-motion"});Assert.IsNotNull(why);StringAssert.Contains("motion",why);StringAssert.Contains("uncovered-motion",why);}
    [Test] public void UncoveredEaseNamesItsFormula() {var why=Check(eases:new[]{"uncovered-ease"});Assert.IsNotNull(why);StringAssert.Contains("ease",why);StringAssert.Contains("uncovered-ease",why);}
    [Test] public void UncoveredCameraFormulaNamesItsId() {var why=Check(formulas:new[]{"uncovered-camera-formula"});Assert.IsNotNull(why);StringAssert.Contains("formula",why);StringAssert.Contains("uncovered-camera-formula",why);}
    [Test] public void MissingParamNamesTheMissingClaim() {var why=Check(implemented:ImplementedParams.Where(p=>p!="shake"));Assert.IsNotNull(why);StringAssert.Contains("missing",why);StringAssert.Contains("shake",why);}
    [Test] public void ExtraParamNamesTheExtraClaim() {var why=Check(implemented:ImplementedParams.Concat(new[]{"extra-param"}));Assert.IsNotNull(why);StringAssert.Contains("extra",why);StringAssert.Contains("extra-param",why);}
    [Test] public void ParamRefusalNamesBothSetDifferences() {var why=Check(implemented:ImplementedParams.Where(p=>p!="shake").Concat(new[]{"extra-param"}));StringAssert.Contains("missing: shake",why);StringAssert.Contains("extra: extra-param",why);}
    [Test] public void FirstRefusalFollowsVersionMotionEaseFormulaParamsOrder() {
        var version=Check(version:Version(2,0),motions:new[]{"uncovered-motion"},eases:new[]{"uncovered-ease"},formulas:new[]{"uncovered-formula"},implemented:new string[0]);StringAssert.Contains("2.0",version);
        var motion=Check(motions:new[]{"uncovered-motion"},eases:new[]{"uncovered-ease"},formulas:new[]{"uncovered-formula"},implemented:new string[0]);StringAssert.Contains("uncovered-motion",motion);
        var ease=Check(eases:new[]{"uncovered-ease"},formulas:new[]{"uncovered-formula"},implemented:new string[0]);StringAssert.Contains("uncovered-ease",ease);
        var formula=Check(formulas:new[]{"uncovered-formula"},implemented:new string[0]);StringAssert.Contains("uncovered-formula",formula);
    }
    [Test] public void AppliedCameraValuesMatchTheRuntimeGraphNotInlinedConsts() {
        var camera=_rig.DirectorCamera;
        Assert.AreEqual(EngineContract.Current.Camera.Rig.NearClip,camera.nearClipPlane,"nearClip skew");
        Assert.AreEqual(EngineContract.Current.Camera.Rig.FarClip,camera.farClipPlane,"farClip skew");
        Assert.AreEqual(EngineContract.Current.Camera.Lens.DefaultFov,camera.fieldOfView,"defaultFov skew");
    }
    [Test] public void RealAdapterStampsBelongToEngineRowsAndEnumeratedValues() {
        _root.AddComponent<OfflinePresentationScene>();var adapter=_root.AddComponent<GenericSceneAdapter>();var stamps=adapter.Manifest.Capabilities;
        CollectionAssert.AreEquivalent(new[]{CapabilityKeys.DirectorMode,CapabilityKeys.PresentationSourceFingerprint,CapabilityKeys.ProjectSourceRoot},stamps.Keys);
        foreach(var stamp in stamps) {
            var row=CapabilitySpecs.Find(stamp.Key);Assert.IsNotNull(row,stamp.Key);
            Assert.IsTrue(row.Declarer==CapabilityDeclarers.Engine||row.Declarer==CapabilityDeclarers.AdapterOrEngine,stamp.Key);
            if(row.Kind==CapabilityKinds.Enum)CollectionAssert.Contains(row.Values.Select(v=>v.Value),stamp.Value,stamp.Key);
            CollectionAssert.IsEmpty(row.Problems(stamp.Value),stamp.Key);
        }
        Assert.IsFalse(stamps.ContainsKey(CapabilityKeys.CameraParams),"Unity uses the built-in table rather than stamping a restricted param vocabulary");
    }
    [Test] public void RefusingAwakeDisablesBridgeBeforeAnyListenerOrRuntimeOpens() {
        _root.SetActive(false);var bridge=_root.AddComponent<DirectorBridgeServer>();var why=Check(motions:new[]{"uncovered-motion"});
        var seam=typeof(DirectorBridgeServer).GetField("_contractRefusal",PrivateInstance);Assert.IsNotNull(seam,"startup refusal seam");seam.SetValue(bridge,new Func<string>(()=>why));
        LogAssert.Expect(LogType.Error,"[GameDirector] Bridge refused: "+why);
        typeof(DirectorBridgeServer).GetMethod("Awake",PrivateInstance).Invoke(bridge,null);
        typeof(DirectorBridgeServer).GetMethod("OnEnable",PrivateInstance).Invoke(bridge,null);
        Assert.IsFalse(bridge.enabled);
        foreach(var name in new[]{"_listener","_thread","_runtime"})Assert.IsNull(typeof(DirectorBridgeServer).GetField(name,PrivateInstance).GetValue(bridge),name);
        Assert.IsNull(_root.GetComponent<DirectorRuntime>());
    }
    [Test] public void BridgeDefaultRefusalDelegateIsLiveBeforeAnyOverride() {
        _root.SetActive(false);var bridge=_root.AddComponent<DirectorBridgeServer>();
        var seam=typeof(DirectorBridgeServer).GetField("_contractRefusal",PrivateInstance);var live=(Func<string>)seam.GetValue(bridge);
        Assert.AreEqual(typeof(UnityEngineContract).GetMethod(nameof(UnityEngineContract.Refusal),Type.EmptyTypes),live.Method,"fresh bridge must use the live cached refusal");
        Assert.IsNull(live(),"shipped Core DLL must be accepted through the production default delegate");
    }
    [Test] public void RefusedBridgeCannotBeReenabledAndLogsOnlyOnce() {
        _root.SetActive(false);var bridge=_root.AddComponent<DirectorBridgeServer>();var why=Check(motions:new[]{"uncovered-motion"});var checks=0;
        typeof(DirectorBridgeServer).GetField("_contractRefusal",PrivateInstance).SetValue(bridge,new Func<string>(()=>{checks++;return why;}));
        LogAssert.Expect(LogType.Error,"[GameDirector] Bridge refused: "+why);
        try {
            typeof(DirectorBridgeServer).GetMethod("Awake",PrivateInstance).Invoke(bridge,null);
            for(var i=0;i<2;i++) {
                bridge.enabled=true;typeof(DirectorBridgeServer).GetMethod("OnEnable",PrivateInstance).Invoke(bridge,null);
                Assert.AreEqual(i+2,checks,"OnEnable must re-check the cached refusal seam before the missing-runtime guard");
                Assert.IsFalse(bridge.enabled,"a refused bridge must disable itself on re-enable");
                foreach(var name in new[]{"_listener","_thread","_runtime"})Assert.IsNull(typeof(DirectorBridgeServer).GetField(name,PrivateInstance).GetValue(bridge),name);
                Assert.IsNull(_root.GetComponent<DirectorRuntime>());
            }
            LogAssert.NoUnexpectedReceived();
        } finally {typeof(DirectorBridgeServer).GetMethod("OnDisable",PrivateInstance).Invoke(bridge,null);}
    }
    [Test] public void BridgeWithoutAnInitializedRuntimeCannotOpenAListener() {
        _root.SetActive(false);var bridge=_root.AddComponent<DirectorBridgeServer>();
        try {
            bridge.enabled=true;typeof(DirectorBridgeServer).GetMethod("OnEnable",PrivateInstance).Invoke(bridge,null);
            Assert.IsFalse(bridge.enabled,"missing runtime must disable the bridge before opening a listener");
            foreach(var name in new[]{"_listener","_thread","_runtime"})Assert.IsNull(typeof(DirectorBridgeServer).GetField(name,PrivateInstance).GetValue(bridge),name);
        } finally {typeof(DirectorBridgeServer).GetMethod("OnDisable",PrivateInstance).Invoke(bridge,null);}
    }
    public static IEnumerable<TestCaseData> EaseFormulasRows() {
        foreach(var f in typeof(EaseFormulas).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.IsLiteral&&f.FieldType==typeof(string)))
            yield return new TestCaseData((string)f.GetRawConstantValue()).SetName("ImplementedEaseFormula_"+f.Name);
    }
    [TestCaseSource(nameof(EaseFormulasRows))] public void EveryDeclaredEaseFormulaReallyEvaluates(string formula) {
        Assert.IsTrue(CinematicCameraRig.ImplementsEase(formula));
        // Hand-checked dyadic samples at .25, independent of the engine's switch.
        var expected=new Dictionary<string,float>{{EaseFormulas.Identity,.25f},{EaseFormulas.QuadIn,.0625f},{EaseFormulas.QuadOut,.4375f},{EaseFormulas.Smoothstep,.15625f}};
        Assert.AreEqual(expected[formula],Ease(.25f,formula));
    }
    [Test] public void UnknownEaseFormulaThrowsRatherThanSmoothstep() {
        Assert.IsFalse(CinematicCameraRig.ImplementsEase("uncovered-ease"));
        var ex=Assert.Throws<TargetInvocationException>(()=>Ease(.25f,"uncovered-ease"));
        Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);StringAssert.Contains("uncovered-ease",ex.InnerException.Message);
    }
    [TestCase(null)] [TestCase("uncovered-ease-id")] public void UnknownEaseIdNamesTheCauseRatherThanNullReference(string id) {
        var ex=Assert.Throws<InvalidOperationException>(()=>_rig.BeginShot(new ShotSpec {Subject="hero",From="from",Ease=id},name=>_root.transform,
            (string name,out Vector3 position,out float heading)=>{position=new Vector3(0,1,-4);heading=0;return true;}));
        StringAssert.Contains(id??"<null>",ex.Message);
    }
    [TestCase(null)] [TestCase("custom-unregistered")] public void UnregisteredAndNullShotTypesStillHold(string type) {
        var subject=new GameObject("fallback-subject").transform;subject.SetParent(_root.transform,false);
        var from=new Vector3(0,1,-4);_rig.BeginShot(new ShotSpec {Type=type,Subject="hero",From="from",To="to",DurationSeconds=1,Ease="linear"},name=>subject,
            (string name,out Vector3 position,out float heading)=>{position=name=="to"?new Vector3(4,1,-4):from;heading=0;return true;});
        subject.position+=new Vector3(1,0,0);
        _rig.Evaluate(.75);Assert.AreEqual(from,_rig.DirectorCamera.transform.position,"unregistered '"+(type??"<null>")+"' must Hold at e=.75 after the subject steps");
    }
}
}
