using System;
using System.Collections.Generic;

namespace GameDirector.Core.Dsl
{
    public static class CameraRig
    {
        public const float NearClip=.03f, FarClip=1000f, MinSubjectHeight=.1f, DegenerateSqrMagnitude=.01f, AimEpsilonSqrMagnitude=.00001f;
        public const float AutoFrameFallbackUp=.25f, DistanceFallbackUp=.2f, FallbackBack=1f;
        public const string ShakeNoise="unity-perlin-2d", ShakeClock="shot-elapsed-seconds";
        public const float ShakeFrequencyHz=7.3f, ShakeRow=.5f, ShakeCenter=.5f, ShakeScale=.05f;
    }
    public static class EaseFormulas{public const string Identity="identity", QuadIn="quad-in", QuadOut="quad-out", Smoothstep="smoothstep";}
    public static class MotionFormulas{public const string Hold="hold", Lerp="lerp", OrbitUp="orbit-up", TrackOffset="track-offset";}
    public static class CameraFormulas{public const string Progress="clamp01-linear", FocalToFov="vertical-2atan", AutoFrameDistance="height-over-coverage", Aspect="hor-plus";}
    public static class ParamDerivations{public const string SubjectBoundsCenter="subject-bounds-center", FrameCoverage="frame-coverage", StartFov="start-fov";}
    public static class MotionObservables
    {
        public const string Displacement="displacement", Fraction="fraction", Radius="radius", Radius3D="radius3d", SweptDeg="swept-deg", Side="side", Drift="drift";
    }
    public sealed class EaseSample
    {
        internal EaseSample(float p,float e){P=p;E=e;}
        public float P{get;} public float E{get;}
    }
    public sealed class EaseSpec
    {
        internal EaseSpec(string id,string formula,string expression,params EaseSample[] samples){Id=id;Formula=formula;Expression=expression;Samples=samples;}
        public string Id{get;} public string Formula{get;} public string Expression{get;} public IReadOnlyList<EaseSample> Samples{get;}
    }
    public sealed class MotionSample
    {
        internal MotionSample(string ease,float p,string observable,float expected,float tolerance,IReadOnlyDictionary<string,float> parameters=null,bool authoredTo=true,bool stepSubject=false)
        {Ease=ease;P=p;Observable=observable;Expected=expected;Tolerance=tolerance;Params=parameters;AuthoredTo=authoredTo;StepSubject=stepSubject;}
        public string Ease{get;} public float P{get;} public IReadOnlyDictionary<string,float> Params{get;} public bool AuthoredTo{get;} public bool StepSubject{get;}
        public string Observable{get;} public float Expected{get;} public float Tolerance{get;}
    }
    public sealed class MotionSpec
    {
        internal MotionSpec(string id,string expression,string meaning,string[] reads,params MotionSample[] samples){Id=id;Expression=expression;Meaning=meaning;Reads=reads;Samples=samples;}
        public string Id{get;} public string Expression{get;} public string Meaning{get;} public IReadOnlyList<string> Reads{get;} public IReadOnlyList<MotionSample> Samples{get;}
    }
    public static class CameraMotions
    {
        public static IReadOnlyList<MotionSpec> All=>Table;
        public static MotionSpec Find(string id)=>id==null?null:Array.Find(Table,m=>m.Id==id);
        private static readonly MotionSpec[] Table={
            new MotionSpec(MotionFormulas.Hold,"pos = from","Hold the resolved start position, even with an authored to.",Array.Empty<string>(),
                new MotionSample("inOut",.5f,MotionObservables.Displacement,0,0),new MotionSample("linear",1,MotionObservables.Displacement,0,0)),
            new MotionSpec(MotionFormulas.Lerp,"pos = lerp(from, to, e)","Interpolate to the resolved authored destination (dolly and crane).",Array.Empty<string>(),
                new MotionSample("linear",.25f,MotionObservables.Fraction,.25f,0),new MotionSample("inOut",.25f,MotionObservables.Fraction,.15625f,0),
                new MotionSample("in",.5f,MotionObservables.Fraction,.25f,0),new MotionSample("out",.5f,MotionObservables.Fraction,.75f,0),
                new MotionSample("inOut",1,MotionObservables.Fraction,1,0),new MotionSample("inOut",.75f,MotionObservables.Fraction,.84375f,0)),
            new MotionSpec(MotionFormulas.OrbitUp,"pos = target(t) + rotateUp(from - target0, orbitDeg * e)","Orbit about world up; + carries the camera toward its own left (clockwise seen from above).",new[]{"orbitDeg"},
                new MotionSample("linear",.5f,MotionObservables.Radius,4,1e-4f,new Dictionary<string,float>{{"orbitDeg",90}},authoredTo:false),
                new MotionSample("linear",.5f,MotionObservables.SweptDeg,45,1e-3f,new Dictionary<string,float>{{"orbitDeg",90}},authoredTo:false),
                new MotionSample("linear",.5f,MotionObservables.Side,-1,0,new Dictionary<string,float>{{"orbitDeg",90}},authoredTo:false),
                new MotionSample("inOut",1,MotionObservables.SweptDeg,90,1e-3f,authoredTo:false),new MotionSample("inOut",1,MotionObservables.Side,-1,0,authoredTo:false),
                new MotionSample("linear",.5f,MotionObservables.SweptDeg,30,1e-3f,new Dictionary<string,float>{{"orbitDeg",-60}},authoredTo:false),
                new MotionSample("linear",.5f,MotionObservables.Side,1,0,new Dictionary<string,float>{{"orbitDeg",-60}},authoredTo:false)),
            new MotionSpec(MotionFormulas.TrackOffset,"pos = target(t) + (from - target0)","Follow the subject while preserving the start offset.",Array.Empty<string>(),
                new MotionSample("linear",.5f,MotionObservables.Drift,0,1e-5f,authoredTo:false,stepSubject:true))
        };
    }
    public sealed class CameraSpec
    {
        public string CurrentAnchor=>ShotVocabulary.CurrentAnchor;
        public LensSpec Lens{get;}=new LensSpec();
        public IReadOnlyList<FrameBehavior> Frames=>ShotVocabulary.Frames; public string FallbackFrame=>ShotVocabulary.FallbackFrameId;
        public FormulaSpec Progress{get;}=new FormulaSpec(CameraFormulas.Progress,
            "p = durationSeconds > 0 ? clamp01((t - shotStart) / durationSeconds) : 0; e = ease(p); the camera runs on the unscaled timeline clock (world.timescale scales the world only)");
        public IReadOnlyList<EaseSpec> Eases=>ShotVocabulary.EaseSpecs; public IReadOnlyList<MotionSpec> Motions=>CameraMotions.All;
        public IReadOnlyList<ShotBehavior> Shots=>ShotVocabulary.Shots; public IReadOnlyList<ShotParam> Params=>ShotVocabulary.Params;
        public IReadOnlyDictionary<string,string> Derivations=>ShotVocabulary.Derivations;
        public AimSpec Aim{get;}=new AimSpec(); public RigSpec Rig{get;}=new RigSpec(); public ShakeSpec Shake{get;}=new ShakeSpec();
        public ConformanceScene Scene{get;}=new ConformanceScene();
    }
    public sealed class FocalSample
    {
        internal FocalSample(float mm,float fov,float tolerance){FocalLengthMm=mm;Fov=fov;Tolerance=tolerance;}
        public float FocalLengthMm{get;} public float Fov{get;} public float Tolerance{get;}
    }
    public sealed class LensSpec
    {
        public float DefaultFov=>ShotVocabulary.DefaultFov; public float SensorSizeMm=>ShotVocabulary.SensorSizeMm;
        public float MinFov=>ShotVocabulary.MinFov; public float MaxFov=>ShotVocabulary.MaxFov;
        public string Resolution=>"fov, else focalToFov(focalLengthMm), else defaultFov";
        public FormulaSpec FocalToFov{get;}=new FormulaSpec(CameraFormulas.FocalToFov,"fovDeg = degrees(2*atan(sensorSizeMm/(2*focalLengthMm))) (vertical)");
        public IReadOnlyList<FocalSample> Samples{get;}=new[]{new FocalSample(50,26.991467f,1e-3f),new FocalSample(35,37.849289f,1e-3f)};
        public AspectLensSpec Aspect{get;}=new AspectLensSpec();
    }
    public sealed class ViewSample
    {
        internal ViewSample(float pan,float tilt,float roll,float x,float y):this(pan,tilt,roll,1,x,y){}
        internal ViewSample(float pan,float tilt,float roll,float aspect,float x,float y){Pan=pan;Tilt=tilt;Roll=roll;Aspect=aspect;X=x;Y=y;}
        public float Pan{get;} public float Tilt{get;} public float Roll{get;} public float Fov=>ShotVocabulary.DefaultFov; public float Aspect{get;}
        public float X{get;} public float Y{get;} public float Tolerance=>1e-4f; public float LiveTolerance=>2e-2f;
    }
    public sealed class AspectLensSpec
    {
        public string Id=>CameraFormulas.Aspect;
        public string Expression=>"fov is vertical at every aspect; tan(hfov/2)=tan(fov/2)*width/height";
        public IReadOnlyList<ViewSample> Samples{get;}=Build();
        private static ViewSample[] Build()
        {
            var samples=new List<ViewSample>();
            foreach (var angles in new[]{new[]{5f,0,0},new[]{10f,0,0},new[]{20f,15,30}}) foreach (var aspect in new[]{9f/16,16f/9}) {
                var v=CameraReference.Viewport(angles[0],angles[1],angles[2],ShotVocabulary.DefaultFov,aspect);
                samples.Add(new ViewSample(angles[0],angles[1],angles[2],aspect,(float)v[0],(float)v[1]));
            }
            return samples.ToArray();
        }
    }
    public sealed class AimSpec
    {
        public string Order=>"LookRotation(aim) * Euler(tilt, pan, roll): intrinsic yaw(pan), pitch(tilt), roll(roll); signs viewer-relative";
        public IReadOnlyList<ViewSample> Samples{get;}=new[]{new ViewSample(0,0,0,.5f,.5f),new ViewSample(20,0,0,.109732f,.5f),new ViewSample(-20,0,0,.890268f,.5f),
            new ViewSample(0,15,0,.5f,.787309f),new ViewSample(20,0,30,.162018f,.695134f),new ViewSample(20,15,30,.293750f,.950835f),
            new ViewSample(0,-10,45,.366309f,.366309f),new ViewSample(0,0,45,.5f,.5f)};
    }
    public sealed class FallbackOffset
    {
        internal FallbackOffset(float up,float back){Up=up;Back=back;}
        public float Up{get;} public float Back{get;}
    }
    public sealed class AutoFrameSample
    {
        internal AutoFrameSample(string frame,float? coverage,float radius3D,float tolerance){Frame=frame;Coverage=coverage;Radius3D=radius3D;Tolerance=tolerance;}
        public string Frame{get;} public float? Coverage{get;} public float Radius3D{get;} public float Tolerance{get;}
    }
    public sealed class RigSpec
    {
        public float NearClip=>CameraRig.NearClip; public float FarClip=>CameraRig.FarClip; public float MinSubjectHeight=>CameraRig.MinSubjectHeight;
        public float DegenerateSqrMagnitude=>CameraRig.DegenerateSqrMagnitude; public float AimEpsilonSqrMagnitude=>CameraRig.AimEpsilonSqrMagnitude;
        public FallbackOffset AutoFrameFallback{get;}=new FallbackOffset(CameraRig.AutoFrameFallbackUp,CameraRig.FallbackBack);
        public FallbackOffset DistanceFallback{get;}=new FallbackOffset(CameraRig.DistanceFallbackUp,CameraRig.FallbackBack);
        public string BackAxis=>"back is opposite the engine's forward: Unity -Z, Three +Z";
        public FormulaSpec AutoFrameDistance{get;}=new FormulaSpec(CameraFormulas.AutoFrameDistance,
            "from:current and distance <= 0 only: max(minSubjectHeight, subjectHeight) / (2*tan(fov/2)*coverage) along (start - target), or autoFrameFallback when lengthSq < degenerateSqrMagnitude");
        public IReadOnlyList<AutoFrameSample> Samples{get;}=new[]{new AutoFrameSample("full",null,2.680634f,1e-3f),new AutoFrameSample("full",1.5f,1.429671f,1e-3f)};
    }
    public sealed class ShakeSpec
    {
        public string Noise=>CameraRig.ShakeNoise; public string Clock=>CameraRig.ShakeClock; public float FrequencyHz=>CameraRig.ShakeFrequencyHz;
        public float Row=>CameraRig.ShakeRow; public float Center=>CameraRig.ShakeCenter; public float Scale=>CameraRig.ShakeScale;
        public string Expression=>"position += up * (((noise(elapsed*frequencyHz, row) - center) * scale) * shake)";
    }
    public sealed class ConformanceScene
    {
        public IReadOnlyList<float> SubjectSize=>new[]{1f,2f,1f}; public IReadOnlyList<float> SubjectCenter=>new[]{0f,1f,0f};
        public IReadOnlyList<float> From=>new[]{0f,1f,-4f}; public IReadOnlyList<float> To=>new[]{4f,1f,-4f}; public IReadOnlyList<float> SubjectStep=>new[]{1f,0f,0f};
        public double DurationSeconds=>1; public float Aspect=>1f;
        public string TargetHeightNote=>"The centered base fixture makes omitted targetHeight equivalent to targetHeight=1 only because the bounds-center offset has zero X/Z. Also characterize an off-center subject: with pivot (0,0,0) and bounds center (1,1,0.5), omission keeps offset (1,1,0.5), while targetHeight=1 uses (0,1,0); these poses must differ.";
        public IReadOnlyDictionary<string,string> Observables=>ObservableTable;
        private static readonly IReadOnlyDictionary<string,string> ObservableTable=new Dictionary<string,string>{
            {MotionObservables.Fraction,"|pos-from|/|to-from|"},{MotionObservables.Displacement,"|pos-from|"},
            {MotionObservables.Radius,"horizontal |pos-target|"},{MotionObservables.Radius3D,"|pos-target|"},
            {MotionObservables.SweptDeg,"unsigned horizontal angle between (from-target0) and (pos-target)"},
            {MotionObservables.Side,"sign(dot(pos-pos0, cameraRight at p=0))"},{MotionObservables.Drift,"|(pos-target(t))-(from-target0)|"}
        };
    }
}
