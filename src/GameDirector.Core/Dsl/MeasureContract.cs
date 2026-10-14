using System.Collections.Generic;
using GameDirector.Core.Capture;
namespace GameDirector.Core.Dsl
{
    /// <summary>Raw engine observations only; shot provenance and horizon evaluation belong to Core.</summary>
    public sealed class MeasureSpec
    {
        public string Capability=>CapabilityKeys.Measure;
        public string Flag=>"measure";
        public string Collection=>"measures";
        public IReadOnlyList<string> Fields{get;}=new[]{"frame","time","camera.position[3]","camera.right[3]","camera.up[3]","camera.forward[3]","camera.fov","camera.aspect","composition"};
        public string Composition=>"review.roles";
        public string Rule=>"take frames flagged measure are read after render and before advance; composition is exactly the review composition object, including RoleReviewSpec/role-bounds-v1, without restating its fields";
        public string Camera=>"position is the world translation; right and up are world unit vectors pointing image-right and image-up; forward is the world unit look direction; fov is vertical degrees; aspect is width/height";
        public FormulaSpec HorizonRoll{get;}=new FormulaSpec("horizon-roll-v1","degrees(asin(clamp(dot(cameraRight_world, worldUp), -1, 1))); worldUp = (0,1,0); positive has the sign of authored +roll; pitch and pan can make horizonRoll differ from authored roll");
        public IReadOnlyList<HorizonSample> Samples{get;}=new[]{
            new HorizonSample(new[]{0d,0,0},new[]{0d,0,-1},0,0,10),
            new HorizonSample(new[]{0d,0,0},new[]{0d,0,-1},0,30,10),
            new HorizonSample(new[]{6d,3,2.5},new[]{0d,.9,-.5},-15,0,0),
            new HorizonSample(new[]{2d,3,4},new[]{-1d,.5,-2},17,-23,31)};
    }
    public sealed class HorizonSample
    {
        internal HorizonSample(double[] from,double[] aim,double pan,double tilt,double roll)
        {From=from;Aim=aim;Pan=pan;Tilt=tilt;Roll=roll;Right=CameraReference.WorldRight(from,aim,pan,tilt,roll);Up=CameraReference.WorldUp(from,aim,pan,tilt,roll);Forward=CameraReference.WorldForward(from,aim,pan,tilt,roll);HorizonRoll=MeasureEvaluation.HorizonRoll(Right);}
        public IReadOnlyList<double> From{get;} public IReadOnlyList<double> Aim{get;}
        public double Pan{get;} public double Tilt{get;} public double Roll{get;}
        public IReadOnlyList<double> Right{get;} public IReadOnlyList<double> Up{get;} public IReadOnlyList<double> Forward{get;}
        public double HorizonRoll{get;}
        // Absolute conformance bounds: unitless basis components and horizon degrees.
        public double Tolerance=>1e-9; public double LiveTolerance=>2e-2;
    }
}
