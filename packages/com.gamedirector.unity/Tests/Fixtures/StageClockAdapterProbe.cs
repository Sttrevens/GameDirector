using GameDirector.Core.Dsl;
using GameDirector.Unity;
using UnityEngine;
namespace GameDirector.Unity.Tests {
public sealed class StageClockAdapterProbe : GameDirectorAdapterBase {
    public bool BorrowActor;
    public bool ProposeAnimation;
    public Component Exempt;
    protected override bool IsStageClockExempt(Component c) => c==Exempt;
    protected override bool IsAutonomousClock(Component c) => base.IsAutonomousClock(c) || c is AutonomousClockProbe p && p.enabled;
    // Exposes the base refusal helper so tests pin its classification once.
    public System.Exception Refuse(string message) => RefuseStage(message);
    public void Initialize() => base.Awake();
    public Transform Camera => Rig.DirectorCamera.transform;
    protected override CapabilityManifest BuildManifest() {
        var m=new CapabilityManifest {Game="stage-clock-probe",ShotTypes=ShotVocabulary.ShotIds,FrameTypes=ShotVocabulary.FrameIds};
        m.Capabilities[CapabilityKeys.DirectorMode]=DirectorModes.OfflineSandbox;
        if(BorrowActor) {
            m.Roles.Add(new RoleDescriptor {Id="hero",DefaultActor="body",PresentAtStart=true});
            var actor=new ActorDescriptor {Id="body"};
            if(ProposeAnimation)actor.Clips.Add("proposed");
            m.Actors.Add(actor);
        }
        return m;
    }
    protected override Transform SpawnRoleInstance(string role,Vector3 position,Quaternion rotation) => null;
}
}
