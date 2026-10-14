using System;
using System.Collections.Generic;
namespace GameDirector.Core.Dsl
{
    public static class RoleReviewVocabulary
    {
        public const string Subject="subject",With="with",LookAt="lookAt";
        public const string StateInFront="in-front",StateBehind="behind",StateAbsent="absent",StateEmpty="empty",StateStraddlesNear="straddles-near",StateError="error";
        public static readonly IReadOnlyList<string> Kinds=new[]{Subject,With,LookAt},States=new[]{StateInFront,StateBehind,StateAbsent,StateEmpty,StateStraddlesNear,StateError};
    }
    /// <summary>Composition payload vocabulary; evaluation thresholds belong to review policy, not this graph.
    /// Engines may omit roles until their bounds implementation ships (planned per-engine role bounds).</summary>
    public sealed class RoleReviewSpec
    {
        public IReadOnlyList<string> Fields{get;}=new[]{"role","kind","state","rect","crown","message"};
        public IReadOnlyList<string> Kinds=>RoleReviewVocabulary.Kinds;
        public IReadOnlyList<string> States=>RoleReviewVocabulary.States;
        public FormulaSpec Bounds{get;}=new FormulaSpec("role-bounds-v1",
            "union of world AABBs in the visible/active subtree; hidden nodes hide descendants; rigid Mesh local geometry bounds times world matrix; SkinnedMesh posed vertices via getVertexPosition; InstancedMesh per-instance matrices; BatchedMesh active visible instances with geometry ranges and matrices computed into locals; Sprite camera-facing world quad (world scale, centre, material rotation and size attenuation); Line and Points excluded; no renderable is empty; no engine boundingBox or boundingSphere cache writes; computation failures report error with message");
        public string Rect=>"unclamped viewport [x0,y0,x1,y1] of all 8 world-AABB corners; omitted unless every corner depth > nearClip";
        public FormulaSpec Crown{get;}=new FormulaSpec("bounds-top-center","viewport [x,y,depth] of ((min.x+max.x)/2,max.y,(min.z+max.z)/2), a head proxy, not the maximum projected top corner");
        public string Rule=>"one entry for subject, each with role and a distinct lookAt; absent roles never require a live role binding; behind means all corners have depth <= 0; straddles-near means not behind but at least one corner depth <= nearClip (rect omitted); errors never fail a take; aim uses each engine's legacy bounds; reviews use role-bounds-v1";
        public IReadOnlyList<RoleBoundsSample> Samples{get;}=Build();
        private static RoleBoundsSample[] Build()
        {
            var rows=new List<RoleBoundsSample>();var min=new[]{-.5d,0,-.5};var max=new[]{.5d,2,.5};var from=new[]{0d,1,-4};var aim=new[]{0d,1,0};
            foreach (var angles in new[]{new[]{5d,0,0},new[]{10d,0,0},new[]{20d,15,30}}) foreach (var aspect in new[]{9d/16,16d/9})
                rows.Add(new RoleBoundsSample(min,max,from,aim,aspect,angles[0],angles[1],angles[2]));
            rows.Add(new RoleBoundsSample(new[]{-.5d,0,-8.5},new[]{.5d,2,-7.5},from,aim,1,0,0,0));
            rows.Add(new RoleBoundsSample(new[]{-.5d,0,-4.02},new[]{.5d,2,-3.9},from,aim,1,0,0,0));
            return rows.ToArray();
        }
    }
    public sealed class RoleBoundsSample
    {
        internal RoleBoundsSample(double[] min,double[] max,double[] from,double[] aim,double aspect,double pan,double tilt,double roll)
        {
            Min=min;Max=max;From=from;Aim=aim;Aspect=Math.Round(aspect,9);Pan=pan;Tilt=tilt;Roll=roll;
            var projection=CameraReference.Bounds(min,max,from,aim,Fov,aspect,pan,tilt,roll);State=projection.State;Rect=Published(projection.Rect);Crown=Published(projection.Crown);
        }
        // Wire samples, not the reference kernel: fixed decimal quantization
        // absorbs platform libm last-bit drift without changing engine poses.
        private static IReadOnlyList<double> Published(IReadOnlyList<double> values)
        {
            if(values==null)return null;
            var rounded=new double[values.Count];for(int i=0;i<rounded.Length;i++)rounded[i]=Math.Round(values[i],9);
            return rounded;
        }
        public IReadOnlyList<double> Min{get;} public IReadOnlyList<double> Max{get;} public IReadOnlyList<double> From{get;} public IReadOnlyList<double> Aim{get;}
        public double Fov=>ShotVocabulary.DefaultFov; public double Aspect{get;} public double Pan{get;} public double Tilt{get;} public double Roll{get;}
        public string State{get;} public IReadOnlyList<double> Rect{get;} public IReadOnlyList<double> Crown{get;}
        public double Tolerance=>1e-4; public double LiveTolerance=>2e-2;
    }
}
