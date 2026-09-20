using GameDirector.Unity;
using GameDirector.Core.Dsl;
using UnityEngine;
namespace GameDirector.Unity.Tests {
public sealed class SpawnParticipantAdapterProbe : GameDirectorAdapterBase {
    public void Initialize() => base.Awake();
    public GameObject Prefab;
    public Transform Actor;
    protected override GameObject SpawnPrefab(string role) => Prefab;
    protected override bool IsAnimationBound(string role,string clip) => role=="spawned" && clip=="wave" && Prefab!=null && Prefab.GetComponent<ClockedVisualProbe>()!=null;
    public override void PlayAnimation(string role,string clip,float fade) {
        if(!IsAnimationBound(role,clip))throw new System.InvalidOperationException("unbound procedural clip");
        RequireRole(role).GetComponent<ClockedVisualProbe>().Pose=clip;
    }
    protected override CapabilityManifest BuildManifest() {
        var m=new CapabilityManifest {Game="probe"};
        m.Capabilities["director.mode"]="offline-sandbox";
        m.Roles.Add(new RoleDescriptor{Id="spawned",DefaultActor="scripted",PresentAtStart=false});
        m.Actors.Add(new ActorDescriptor{Id="scripted",Clips=new System.Collections.Generic.List<string>{"wave"}});
        m.Locations.Add(new LocationDescriptor {Id="origin",Position=new float[]{0,0,0}});
        return m;
    }
    protected override Transform SpawnRoleInstance(string role,Vector3 position,Quaternion rotation) => Actor=PresentationMode.CreateActor(Prefab,position,rotation).transform;
}

}
