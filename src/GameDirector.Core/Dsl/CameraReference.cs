using System;
using System.Collections.Generic;
namespace GameDirector.Core.Dsl
{
    /// <summary>Double-precision reference only; no engine pose code calls it. Coordinates use
    /// Unity's +Z forward, viewport Y up. The contract generates samples here, tests cross-check old literals.</summary>
    public static class CameraReference
    {
        public static double[] Viewport(double pan,double tilt,double roll,double fov,double aspect) =>
            Project(new[]{0d,0,1},new[]{0d,0,0},new[]{0d,0,1},fov,aspect,pan,tilt,roll);
        public static double AutoFrameRadius(double height,double coverage,double fov) =>
            Math.Max(CameraRig.MinSubjectHeight,height)/(2*Math.Tan(fov*Math.PI/360)*coverage);
        public static double[] Project(IReadOnlyList<double> point,IReadOnlyList<double> from,IReadOnlyList<double> aim,double fov,double aspect,double pan=0,double tilt=0,double roll=0)
        {
            var forward=Unit(Sub(aim,from));var right=Unit(Cross(new[]{0d,1,0},forward));var up=Cross(forward,right);var delta=Sub(point,from);
            double x=Dot(delta,right),y=Dot(delta,up),z=Dot(delta,forward);
            double p=pan*Math.PI/180,t=tilt*Math.PI/180,r=roll*Math.PI/180;
            var yawX=Math.Cos(p)*x-Math.Sin(p)*z;z=Math.Sin(p)*x+Math.Cos(p)*z;x=yawX;
            var pitchY=Math.Cos(t)*y+Math.Sin(t)*z;z=-Math.Sin(t)*y+Math.Cos(t)*z;y=pitchY;
            var rollX=Math.Cos(r)*x+Math.Sin(r)*y;y=-Math.Sin(r)*x+Math.Cos(r)*y;x=rollX;
            var tangent=Math.Tan(fov*Math.PI/360);
            return new[]{.5+.5*x/(z*tangent*aspect),.5+.5*y/(z*tangent),z};
        }
        public static RoleProjection Bounds(IReadOnlyList<double> min,IReadOnlyList<double> max,IReadOnlyList<double> from,IReadOnlyList<double> aim,double fov,double aspect,double pan=0,double tilt=0,double roll=0)
        {
            double x0=double.PositiveInfinity,y0=x0,x1=double.NegativeInfinity,y1=x1;bool beyond=true,behind=true;
            for (int i=0;i<8;i++) {
                var v=Project(new[]{(i&1)==0?min[0]:max[0],(i&2)==0?min[1]:max[1],(i&4)==0?min[2]:max[2]},from,aim,fov,aspect,pan,tilt,roll);
                beyond &= v[2]>CameraRig.NearClip;behind &= v[2]<=0;
                x0=Math.Min(x0,v[0]);y0=Math.Min(y0,v[1]);x1=Math.Max(x1,v[0]);y1=Math.Max(y1,v[1]);
            }
            var crown=Project(new[]{(min[0]+max[0])/2,max[1],(min[2]+max[2])/2},from,aim,fov,aspect,pan,tilt,roll);
            return new RoleProjection(behind?RoleReviewVocabulary.StateBehind:beyond?RoleReviewVocabulary.StateInFront:RoleReviewVocabulary.StateStraddlesNear,beyond?new[]{x0,y0,x1,y1}:null,crown);
        }
        /// <summary>Reference image-right world basis (Three's -Z camera convention); no engine calls this.</summary>
        public static double[] WorldRight(IReadOnlyList<double> from,IReadOnlyList<double> aim,double pan,double tilt,double roll)=>WorldBasis(from,aim,pan,tilt,roll)[0];
        public static double[] WorldUp(IReadOnlyList<double> from,IReadOnlyList<double> aim,double pan,double tilt,double roll)=>WorldBasis(from,aim,pan,tilt,roll)[1];
        public static double[] WorldForward(IReadOnlyList<double> from,IReadOnlyList<double> aim,double pan,double tilt,double roll)=>WorldBasis(from,aim,pan,tilt,roll)[2];
        // One reference rotation: lookAt, then local Y(-pan), X(-tilt), Z(roll).
        private static double[][] WorldBasis(IReadOnlyList<double> from,IReadOnlyList<double> aim,double pan,double tilt,double roll)
        {
            var f=Unit(Sub(aim,from));var r=Unit(Cross(f,new[]{0d,1,0}));var u=Cross(r,f);
            double p=pan*Math.PI/180,t=tilt*Math.PI/180,z=roll*Math.PI/180;
            double cp=Math.Cos(p),sp=Math.Sin(p),ct=Math.Cos(t),st=Math.Sin(t),cz=Math.Cos(z),sz=Math.Sin(z);
            double[] V(double a,double b,double c)=>new[]{a*r[0]+b*u[0]+c*f[0],a*r[1]+b*u[1]+c*f[1],a*r[2]+b*u[2]+c*f[2]};
            return new[]{V(cz*cp+sz*st*sp,sz*ct,-cz*sp+sz*st*cp),V(-sz*cp+cz*st*sp,cz*ct,sz*sp+cz*st*cp),V(ct*sp,-st,ct*cp)};
        }
        private static double[] Sub(IReadOnlyList<double> a,IReadOnlyList<double> b) => new[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
        private static double Dot(IReadOnlyList<double> a,IReadOnlyList<double> b) => a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
        private static double[] Cross(IReadOnlyList<double> a,IReadOnlyList<double> b) => new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
        private static double[] Unit(IReadOnlyList<double> v) { var length=Math.Sqrt(Dot(v,v));return new[]{v[0]/length,v[1]/length,v[2]/length}; }
    }
    public sealed class RoleProjection
    {
        internal RoleProjection(string state,IReadOnlyList<double> rect,IReadOnlyList<double> crown){State=state;Rect=rect;Crown=crown;}
        public string State{get;} public IReadOnlyList<double> Rect{get;} public IReadOnlyList<double> Crown{get;}
    }
}
