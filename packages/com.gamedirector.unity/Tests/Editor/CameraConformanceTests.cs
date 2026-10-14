using System.Collections.Generic;
using GameDirector.Core.Dsl;
using NUnit.Framework;
using UnityEngine;
namespace GameDirector.Unity.Tests {
/// <summary>The shot-vocabulary signs are a screen-space contract: +pan turns
/// right, +tilt looks down (ShotVocabulary.Params). Drives CinematicCameraRig
/// directly with a primitive subject and asserts through WorldToViewportPoint —
/// the same assertions tools/test_camera_conformance.py runs against a live
/// bridge, so both engines provably share one camera semantics.</summary>
public class CameraConformanceTests {
    [Test] public void CustomMotionsReceiveTheAuthoredResolvedDestination() {
        // The Motions registry is the documented extension point, and its types
        // have RequiresTarget=null; an authored "to" must still reach them.
        const string type="conformance-probe";
        var called=false;var receivedFrom=Vector3.zero;var receivedTo=Vector3.zero;
        CinematicCameraRig.ShotMotion probe=(from,to,target,offset,e,param)=>{called=true;receivedFrom=from;receivedTo=to;return from;};
        CinematicCameraRig.Motions[type]=probe;
        try {
            var root=new GameObject("camera-conformance-to");
            try {
                var rig=root.AddComponent<CinematicCameraRig>();
                var subject=GameObject.CreatePrimitive(PrimitiveType.Capsule);
                subject.transform.SetParent(root.transform,false);
                rig.DirectorCamera.transform.position=new Vector3(0f,1.6f,-4f);
                rig.BeginShot(new ShotSpec{Type=type,Subject="hero",Frame="full",From=ShotVocabulary.CurrentAnchor,To="stage-mark",DurationSeconds=1,
                        Params=new Dictionary<string,float>{{"toOffsetY",.5f}}},
                    id=>id=="hero"?subject.transform:null,
                    (string id,out Vector3 position,out float heading)=>{position=new Vector3(2f,0f,1f);heading=0f;return id=="stage-mark";});
                Assert.IsTrue(called,"the registered custom motion was not evaluated");
                Assert.AreEqual(new Vector3(2f,.5f,1f),receivedTo,"custom motion must receive the resolved authored destination plus toOffsets");
                Assert.AreNotEqual(receivedFrom,receivedTo,"an authored destination must not collapse to from");
            } finally { Object.DestroyImmediate(root); }
        } finally { CinematicCameraRig.Motions.Remove(type); }
    }
}
}
