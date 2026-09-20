using System;
using System.Collections.Generic;
using GameDirector.Core.Dsl;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>Absolute, frame-clock cinematography. No Update, wall time or accumulated shake.
    /// Shot-type motion is a registry, not a switch: games add a new camera move
    /// by registering a ShotMotion under its manifest id, without editing this rig.
    /// Unregistered types fall back to a static hold, deterministically.</summary>
    public sealed class CinematicCameraRig : MonoBehaviour
    {
        public delegate Transform RoleResolver(string id);
        public delegate bool PoseResolver(string id,out Vector3 position,out float heading);
        /// <summary>Camera position for one evaluated instant. from/to are resolved
        /// anchors, target is the live subject point, offset is from-target, e is
        /// the eased shot progress in [0,1], params the authored shot params.</summary>
        public delegate Vector3 ShotMotion(Vector3 from, Vector3 to, Vector3 target, Vector3 offset, float e, System.Func<string,float,float> param);

        public static readonly IDictionary<string, ShotMotion> Motions = new Dictionary<string, ShotMotion>
        {
            ["dolly"] = (from, to, target, offset, e, param) => Vector3.Lerp(from, to, e),
            ["crane"] = (from, to, target, offset, e, param) => Vector3.Lerp(from, to, e),
            ["orbit"] = (from, to, target, offset, e, param) => target + Quaternion.AngleAxis(param("orbitDeg", 90) * e, Vector3.up) * offset,
            ["tracking"] = (from, to, target, offset, e, param) => target + offset,
        };
        private static Vector3 Hold(Vector3 from, Vector3 to, Vector3 target, Vector3 offset, float e, System.Func<string,float,float> param) => from;
        private static ShotMotion MotionFor(string type) => type != null && Motions.TryGetValue(type, out var motion) ? motion : (ShotMotion)Hold;

        private Camera _camera;
        private ShotSpec _shot;
        private RoleResolver _roles;
        private double _startTime;
        private float _startFov;
        private Vector3 _from,_to,_offset,_subjectCenter;
        public Camera DirectorCamera {
            get {
                if(_camera==null){var go=new GameObject("DirectorCamera");go.transform.SetParent(transform,false);
                    _camera=go.AddComponent<Camera>();_camera.enabled=false;_camera.fieldOfView=ShotVocabulary.DefaultFov;_camera.nearClipPlane=.03f;
                    _camera.farClipPlane=1000;_camera.allowHDR=true;_camera.backgroundColor=new Color(.035f,.045f,.07f);}
                return _camera;
            }
        }
        public Action CaptureState()
        {
            var c=DirectorCamera;var pos=c.transform.position;var rot=c.transform.rotation;var fov=c.fieldOfView;var aspect=c.aspect;
            return ()=>{if(c!=null){c.transform.SetPositionAndRotation(pos,rot);c.fieldOfView=fov;c.aspect=aspect;}EndShot();};
        }
        public bool OwnsObject(Transform candidate) => _camera!=null && candidate==_camera.transform;
        public bool ShotActive=>_shot!=null;
        private float P(string key,float fallback=0)=>_shot.Params!=null && _shot.Params.TryGetValue(key,out var v)?v:fallback;
        public void BeginShot(ShotSpec shot,RoleResolver roles,PoseResolver poses,double timelineTime=0)
        {
            _shot=shot??throw new ArgumentNullException(nameof(shot));_roles=roles;_startTime=timelineTime;
            var subject=roles(shot.Subject);if(subject==null)throw new InvalidOperationException("camera subject unbound: "+shot.Subject);
            var b=BoundsOf(subject);_subjectCenter=b.center-subject.position;
            if(shot.Params!=null && shot.Params.ContainsKey("targetHeight"))_subjectCenter=new Vector3(0,P("targetHeight"),0);
            var camera=DirectorCamera;
            _startFov=shot.Fov??(shot.FocalLengthMm.HasValue?Camera.FocalLengthToFieldOfView(shot.FocalLengthMm.Value,ShotVocabulary.SensorSizeMm):ShotVocabulary.DefaultFov);
            camera.fieldOfView=_startFov;
            if(shot.From=="current")_from=camera.transform.position;
            else if(!poses(shot.From,out _from,out _))throw new InvalidOperationException("camera from unbound: "+shot.From);
            if(shot.From=="current" && P("distance")<=0) {
                // Framing is relative to subject size and lens, while explicit
                // anchors retain their authored spatial composition. Coverage
                // comes from the shared frame vocabulary; a shot may override it.
                float coverage=P("coverage",(float)ShotVocabulary.Coverage(shot.Frame));
                float distance=Mathf.Max(.1f,b.size.y)/(2*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f)*coverage);
                var back=_from-Target();if(back.sqrMagnitude<.01f)back=new Vector3(0,.25f,-1);
                _from=Target()+back.normalized*distance;
            }
            _from+=new Vector3(P("offsetX"),P("offsetY"),P("offsetZ"));
            if(P("distance")>0){var d=_from-Target();if(d.sqrMagnitude<.01f)d=new Vector3(0,.2f,-1);_from=Target()+d.normalized*P("distance");}
            _to=_from;
            if(ShotVocabulary.RequiresTarget(shot.Type)==true) {
                if(shot.To=="current")_to=camera.transform.position;
                else if(!poses(shot.To,out _to,out _))throw new InvalidOperationException("camera to unbound: "+shot.To);
                _to+=new Vector3(P("toOffsetX"),P("toOffsetY"),P("toOffsetZ"));
            }
            _offset=_from-Target();Evaluate(timelineTime);
        }
        private Vector3 Target(){var t=_roles?.Invoke(_shot.Subject);if(t==null)throw new InvalidOperationException("camera subject disappeared: "+_shot.Subject);return t.position+_subjectCenter;}
        public void Evaluate(double time)
        {
            if(_shot==null)return;
            var p=_shot.DurationSeconds>0?Mathf.Clamp01((float)((time-_startTime)/_shot.DurationSeconds)):0;
            var e=Ease(p,_shot.Ease);var target=Target();var position=MotionFor(_shot.Type)(_from,_to,target,_offset,e,P);
            var elapsed=(float)(time-_startTime);
            position+=Vector3.up*((Mathf.PerlinNoise(elapsed*7.3f,.5f)-.5f)*.05f*P("shake"));
            if(!string.IsNullOrEmpty(_shot.LookAt)) {
                var t=_roles(_shot.LookAt);if(t==null)throw new InvalidOperationException("lookAt disappeared");target=BoundsOf(t).center;
            }
            var cam=DirectorCamera.transform;cam.position=position;var direction=target-position;
            if(direction.sqrMagnitude>.00001f)cam.rotation=Quaternion.LookRotation(direction)*Quaternion.Euler(P("tilt"),P("pan"),P("roll"));
            if(_shot.Params!=null && _shot.Params.ContainsKey("endFov"))DirectorCamera.fieldOfView=Mathf.Lerp(_startFov,P("endFov"),e);
        }
        public object InspectFrame()
        {
            if(_shot==null)return new { active=false };
            var subject=_roles(_shot.Subject);var b=BoundsOf(subject);var c=DirectorCamera;var center=c.WorldToViewportPoint(b.center);
            int visible=0,total=9; Physics.SyncTransforms();
            for(int i=0;i<total;i++){
                var p=i==8?b.center:b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
                var v=c.WorldToViewportPoint(p);if(v.z<=0 || v.x<0 || v.x>1 || v.y<0 || v.y>1)continue;
                var d=p-c.transform.position;
                if(!Physics.Raycast(c.transform.position,d.normalized,out var hit,d.magnitude,~0,QueryTriggerInteraction.Ignore) || hit.transform.IsChildOf(subject))visible++;
            }
            return new {active=true,subject=_shot.Subject,shotType=_shot.Type,center=new[]{center.x,center.y,center.z},visibleSamples=visible,totalSamples=total,
                note="Collider visibility probe; inspect actual rendered frame for artistic acceptance."};
        }
        public static Bounds BoundsOf(Transform t)
        {
            var renderers=t.GetComponentsInChildren<Renderer>();Bounds b=new Bounds(t.position+Vector3.up,Vector3.one);bool found=false;
            foreach(var r in renderers)if(r.enabled && !(r is ParticleSystemRenderer)) {
                var bounds=r.bounds;
                if(r is SkinnedMeshRenderer skin && skin.sharedMesh!=null) {
                    // Renderer.bounds can still describe the preceding rendered
                    // pose after Animator.Update(0). Sample the current deformed
                    // mesh so a new shot never latches that stale center.
                    var mesh=new Mesh();
                    try {
                        skin.BakeMesh(mesh,false);
                        var vertices=mesh.vertices;
                        if(vertices.Length>0) {
                            var matrix=skin.localToWorldMatrix;
                            bounds=new Bounds(matrix.MultiplyPoint3x4(vertices[0]),Vector3.zero);
                            for(int i=1;i<vertices.Length;i++)bounds.Encapsulate(matrix.MultiplyPoint3x4(vertices[i]));
                        }
                    } finally { UnityEngine.Object.DestroyImmediate(mesh); }
                }
                if(!found){b=bounds;found=true;}else b.Encapsulate(bounds);
            }
            return b;
        }
        public void EndShot()=>_shot=null;
        private static float Ease(float p,string ease){switch(ease){case "linear":return p;case "in":return p*p;case "out":return 1-(1-p)*(1-p);default:return p*p*(3-2*p);}}
    }
}
