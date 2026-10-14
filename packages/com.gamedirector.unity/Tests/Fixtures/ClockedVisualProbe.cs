using System;
using GameDirector.Unity;
using UnityEngine;
namespace GameDirector.Unity.Tests {
public sealed class ClockedVisualProbe : MonoBehaviour, IDirectorPresentationParticipant {
    public static int Restores;
    public string Pose="idle";
    public double Value = 7;
    public int Steps;
    public Transform Visual;
    public bool FailPrepare;
    public bool FailRestore;
    public DirectorPresentationPhase Phase => DirectorPresentationPhase.AfterAnimation;
    public Action CaptureState() { var value=Value;var pose=Pose;var position=Visual!=null?Visual.localPosition:Vector3.zero; return ()=> {Restores++;Value=value;Pose=pose;if(Visual!=null)Visual.localPosition=position;if(FailRestore)throw new InvalidOperationException("restore fixture");}; }
    public void PreparePresentation() { Value=0; if(FailPrepare)throw new InvalidOperationException("prepare fixture"); }
    public void EvaluatePresentation(double dt,double time) { Value=time;Steps++;if(Visual!=null)Visual.localPosition=Vector3.right*(float)time; }
}

}
