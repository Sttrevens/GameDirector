using System;
using System.Collections.Generic;
using System.Linq;
namespace GameDirector.Core.Capture
{
    public enum RegressionKind { Number,Vector,State }
    public sealed class RegressionMetric
    {
        internal RegressionMetric(string id,string unit,string definition,bool role,bool rect,RegressionKind kind,Func<MeasureRow,CompositionRole,int,ExpectValue> read)
        {Id=id;Unit=unit;Definition=definition;Role=role;Rect=rect;Kind=kind;Read=read;}
        public string Id{get;} public string Unit{get;} public string Definition{get;} public RegressionKind Kind{get;}
        public double Tolerance=>Kind==RegressionKind.State?0:RegressionMetrics.NumericNoise;
        internal bool Role{get;} internal bool Rect{get;} internal Func<MeasureRow,CompositionRole,int,ExpectValue> Read{get;}
    }
    /// <summary>Core-only regression rows, not camera taste, engine vocabulary or a second measurement path.</summary>
    public static class RegressionMetrics
    {
        public const double NumericNoise=1e-6;
        private static ExpectValue Number(double n)=>new ExpectValue{Value=n};
        public static IReadOnlyList<RegressionMetric> All{get;}=new[]{
            new RegressionMetric("figure.h","frame fraction","M01: rect y1-y0; native px round(h*height), midpoint away from zero",true,true,RegressionKind.Number,(m,r,h)=>new ExpectValue{Value=r.Rect[3]-r.Rect[1],Pixels=checked((int)Math.Round((r.Rect[3]-r.Rect[1])*h,MidpointRounding.AwayFromZero))}),
            new RegressionMetric("figure.cx","frame fraction","M02: (rect x0+x1)/2",true,true,RegressionKind.Number,(m,r,h)=>Number((r.Rect[0]+r.Rect[2])/2)),
            new RegressionMetric("figure.cy","frame fraction","M02: (rect y0+y1)/2",true,true,RegressionKind.Number,(m,r,h)=>Number((r.Rect[1]+r.Rect[3])/2)),
            new RegressionMetric("margin","frame fraction","M03: min(x0,y0,1-x1,1-y1), signed and unclamped",true,true,RegressionKind.Number,(m,r,h)=>Number(Math.Min(Math.Min(r.Rect[0],r.Rect[1]),Math.Min(1-r.Rect[2],1-r.Rect[3])))),
            new RegressionMetric("headroom","frame fraction","M04: 1-crown.y; bottom-left viewport, bounds-top-center in RoleReviewContract/RoleReviewSpec.Crown (not rect top)",true,true,RegressionKind.Number,(m,r,h)=>r.Crown==null?null:Number(1-r.Crown[1])),
            new RegressionMetric("state","role state","Exact RoleReviewVocabulary state; retained when geometry is unavailable",true,false,RegressionKind.State,(m,r,h)=>new ExpectValue{Text=r.State}),
            new RegressionMetric("camera.position","world units","M07: just-rendered world translation; vector residual is max absolute component delta",false,false,RegressionKind.Vector,(m,r,h)=>new ExpectValue{Vector=m.Camera.Position.ToArray()}),
            new RegressionMetric("camera.fov","degrees","M07: just-rendered vertical field of view",false,false,RegressionKind.Number,(m,r,h)=>Number(m.Camera.Fov.Value)),
            new RegressionMetric("horizonRoll","degrees","M15: Core horizon-roll-v1 over the measured right vector",false,false,RegressionKind.Number,(m,r,h)=>Number(m.HorizonRoll))};
        public static RegressionMetric Find(string id)=>All.FirstOrDefault(m=>m.Id==id);
        public static ExpectMetrics Project(MeasureRow row,int height)
        {
            if(height<=0||row?.Camera?.Position==null||row.Camera.Position.Count!=3||!row.Camera.Fov.HasValue||
                row.Camera.Position.Any(n=>!Finite(n))||!Finite(row.Camera.Fov.Value)||!Finite(row.HorizonRoll)||!CompositionReview.IsValid(row.Composition))throw new ArgumentException("invalid measure row for regression");
            var result=new ExpectMetrics();
            foreach(var metric in All.Where(m=>!m.Role))result.Values.Add(metric.Id,metric.Read(row,null,height));
            foreach(var role in (row.Composition.Roles??new List<CompositionRole>()).OrderBy(r=>r.Role,StringComparer.Ordinal)){
                var values=new Dictionary<string,ExpectValue>();
                foreach(var metric in All.Where(m=>m.Role)){
                    if(metric.Rect&&role.Rect==null)continue;
                    var value=metric.Read(row,role,height);if(value!=null)values.Add(metric.Id,value);
                }
                result.Roles.Add(role.Role,values);
            }
            return result;
        }
        internal static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    }
}
