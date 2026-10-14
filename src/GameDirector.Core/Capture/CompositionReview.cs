using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    // Review policy, not an engine contract: nothing here changes a rendered pose.
    public sealed class SafeArea
    {
        public SafeArea(double left,double right,double top,double bottom)
        {
            if(new[]{left,right,top,bottom}.Any(v=>double.IsNaN(v)||double.IsInfinity(v)||v<0||v>=1)||left+right>=1||top+bottom>=1)
                throw new ArgumentException("safe-area needs finite nonnegative l,r,t,b insets with a nonempty rect");
            Left=left;Right=right;Top=top;Bottom=bottom;
        }
        public double Left{get;} public double Right{get;} public double Top{get;} public double Bottom{get;}
        public static IReadOnlyDictionary<string,SafeArea> Presets{get;}=new ReadOnlyDictionary<string,SafeArea>(new Dictionary<string,SafeArea>(StringComparer.OrdinalIgnoreCase){["tiktok"]=new SafeArea(.1111,.1111,.0682,.1911)});
    }
    public sealed class CompositionSample
    {
        public int Frame{get;set;} public double Time{get;set;} public CompositionReport Composition{get;set;}
    }
    public sealed class CompositionReport
    {
        public bool Active{get;set;} public string Subject{get;set;} public IReadOnlyList<double> Center{get;set;} public List<CompositionRole> Roles{get;set;}
        public string ShotType{get;set;} public int? VisibleSamples{get;set;} public int? TotalSamples{get;set;} public string Note{get;set;}
    }
    public sealed class CompositionRole
    {
        public string Role{get;set;} public string Kind{get;set;} public string State{get;set;}
        public IReadOnlyList<double> Rect{get;set;} public IReadOnlyList<double> Crown{get;set;} public string Message{get;set;}
    }
    public sealed class CompositionFinding
    {
        public int? Frame{get;set;} public double Time{get;set;} public string Role{get;set;} public string Code{get;set;} public string Severity{get;set;} public string Note{get;set;}
    }
    public sealed class CompositionVerdict
    {
        public List<CompositionFinding> Findings{get;}=new List<CompositionFinding>();
        public int Errors=>Findings.Count(f=>f.Severity==CompositionSeverities.Error); public int Warnings=>Findings.Count(f=>f.Severity==CompositionSeverities.Warning);
    }
    public static class CompositionReview
    {
        /// <summary>One-second rendered samples only: between-sample crops and occlusion need human inspection.
        /// Legacy centre-only reports and failed bounds probes never produce geometric errors.</summary>
        public static CompositionVerdict Evaluate(IEnumerable<CompositionSample> samples,TimelineAsset timeline,SafeArea safeArea=null,CompositionPolicy policy=null)
        {
            policy??=CompositionPolicy.Default;var verdict=new CompositionVerdict();
            foreach(var sample in samples) {
                var c=sample.Composition;if(c==null||!c.Active)continue;
                var active=ActiveShot.CueIndex(timeline,sample.Time);var shot=active.HasValue?timeline.Cues[active.Value].Shot:null;
                void Add(string role,string code,string severity=CompositionSeverities.Warning,string note=null)=>verdict.Findings.Add(new CompositionFinding{Frame=sample.Frame,Time=sample.Time,Role=role,Code=code,Severity=severity,Note=note});
                if(c.Roles==null) {
                    Add(c.Subject,CompositionCodes.Legacy,CompositionSeverities.Warning,"centre-only review; roles unavailable");
                    if(Vector(c.Center,3)&&(c.Center[2]<=0||Outside(c.Center[0],0,1,0)||Outside(c.Center[1],0,1,0)))Add(c.Subject,CompositionCodes.Lost);
                    if(safeArea!=null&&Vector(c.Center,3)&&Unsafe(c.Center,safeArea))Add(c.Subject,CompositionCodes.Unsafe);continue;
                }
                var pair=shot?.With?.Count>0||c.Roles.Any(r=>r.Kind==RoleReviewVocabulary.With);
                foreach(var r in c.Roles) {
                    bool look=r.Kind==RoleReviewVocabulary.LookAt,protectedRole=r.Kind==RoleReviewVocabulary.With||(r.Kind==RoleReviewVocabulary.Subject&&pair);
                    if(r.State==RoleReviewVocabulary.StateAbsent||r.State==RoleReviewVocabulary.StateEmpty)continue;
                    if(r.State==RoleReviewVocabulary.StateError){Add(r.Role,CompositionCodes.BoundsError,CompositionSeverities.Warning,r.Message);continue;}
                    if(r.State==RoleReviewVocabulary.StateStraddlesNear){if(!look)Add(r.Role,CompositionCodes.SideCut,protectedRole?CompositionSeverities.Error:CompositionSeverities.Warning,RoleReviewVocabulary.StateStraddlesNear);continue;}
                    var rect=Vector(r.Rect,4)?r.Rect:null;var crown=Vector(r.Crown,3)?r.Crown:null;
                    if(r.State==RoleReviewVocabulary.StateBehind||(rect!=null&&(rect[2]<0||rect[0]>1||rect[3]<0||rect[1]>1))) {Add(r.Role,CompositionCodes.Lost,look?CompositionSeverities.Warning:CompositionSeverities.Error);continue;}
                    if(look)continue;
                    if(crown!=null&&crown[2]>0&&(Outside(crown[0],0,1,policy.EdgeTolerance)||Outside(crown[1],0,1,policy.EdgeTolerance))) {
                        var coverage=shot?.Params!=null&&shot.Params.TryGetValue("coverage",out var authored)?authored:ShotVocabulary.Coverage(shot?.Frame??ShotVocabulary.OmittedFrameId);
                        var centre=shot!=null&&(shot.Params==null||!shot.Params.ContainsKey("targetHeight"))&&coverage>1;
                        Add(r.Role,CompositionCodes.HeadCut,CompositionSeverities.Warning,centre?"framed-by-centre":null);
                    }
                    if(rect!=null) {
                        var overflow=Math.Max(-rect[0],rect[2]-1);
                        if(protectedRole&&overflow>policy.EdgeTolerance)Add(r.Role,CompositionCodes.SideCut,CompositionSeverities.Error);
                        else if(r.Kind==RoleReviewVocabulary.Subject&&!pair&&overflow>0) {
                            var angles=shot?.Params!=null&&new[]{"pan","tilt","roll"}.Any(shot.Params.ContainsKey);
                            if(angles||overflow>policy.SingleSideFraction*(rect[2]-rect[0]))Add(r.Role,CompositionCodes.SideCut,CompositionSeverities.Warning);
                        }
                    }
                    if(safeArea!=null&&((crown!=null&&crown[2]>0&&Unsafe(crown,safeArea))||(rect!=null&&(rect[0]<safeArea.Left-policy.SafeSideTolerance||rect[2]>1-safeArea.Right+policy.SafeSideTolerance))))Add(r.Role,CompositionCodes.Unsafe);
                }
            }
            return verdict;
        }
        /// <summary>Composition shape/finite/vocabulary validity; legacy centre-only reports remain valid.
        /// Measure adds only the active-subject/roles requirement, not another bounds validator.</summary>
        public static bool IsValid(CompositionReport c)=>c!=null&&(c.Center==null||Vector(c.Center,3))&&
            (c.Roles==null||c.Roles.All(r=>r!=null&&!string.IsNullOrEmpty(r.Role)&&RoleReviewVocabulary.Kinds.Contains(r.Kind)&&RoleReviewVocabulary.States.Contains(r.State)&&
                (r.Rect==null||Vector(r.Rect,4))&&(r.Crown==null||Vector(r.Crown,3))));
        private static bool Vector(IReadOnlyList<double> v,int count)=>v!=null&&v.Count==count&&v.All(n=>!double.IsNaN(n)&&!double.IsInfinity(n));
        private static bool Outside(double v,double min,double max,double tolerance)=>v<min-tolerance||v>max+tolerance;
        private static bool Unsafe(IReadOnlyList<double> v,SafeArea s)=>Outside(v[0],s.Left,1-s.Right,0)||Outside(v[1],s.Bottom,1-s.Top,0);
    }
}
