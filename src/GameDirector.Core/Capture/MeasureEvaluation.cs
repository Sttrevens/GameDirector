using System;
using System.Collections.Generic;
using System.Linq;
using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    /// <summary>The one measure evaluator; engines emit raw observations, never this judgment.</summary>
    public static class MeasureEvaluation
    {
        public static string Refusal(CapabilityManifest manifest)=>CapabilitySpecs.Declared(manifest,CapabilityKeys.Measure)==CapabilityKeys.MeasureV1?null:
            "cannot measure here: manifest does not declare "+CapabilityKeys.Measure+"="+CapabilityKeys.MeasureV1;
        public static IReadOnlyList<MeasureRow> Evaluate(IReadOnlyList<MeasureSample> samples,TimelineAsset resolved,IReadOnlyList<int> authoredCues,IReadOnlyList<int> frames,int fps)
        {
            if(samples==null||samples.Count!=frames.Count)throw new ArgumentException("measures must cover exactly the requested frames (row count mismatch)");
            if(authoredCues.Count!=resolved.Cues.Count)throw new ArgumentException("resolved/authored cue mapping mismatch");
            var requested=new HashSet<int>(frames);var byFrame=new Dictionary<int,MeasureSample>();
            foreach(var s in samples) {
                if(s==null||!s.Frame.HasValue||!requested.Contains(s.Frame.Value)||byFrame.ContainsKey(s.Frame.Value))throw new ArgumentException("measures must cover exactly the requested frames (index mismatch)");
                if(!Valid(s))throw new ArgumentException("malformed or non-finite measure at frame "+s.Frame);
                var time=FrameClock.TimeOf(s.Frame.Value,fps);
                if(Math.Abs(s.Time.Value-time)>FrameClock.AlignmentToleranceFrames/fps)throw new ArgumentException("measure time disagrees with FrameClock at frame "+s.Frame);
                byFrame.Add(s.Frame.Value,s);
            }
            var rows=new List<MeasureRow>();
            foreach(var frame in frames) {
                var sample=byFrame[frame];var time=FrameClock.TimeOf(frame,fps);var active=ActiveShot.CueIndex(resolved,time);
                if(!active.HasValue)throw new ArgumentException("measure frame has no active camera shot: "+frame);
                var cue=resolved.Cues[active.Value];var shot=cue.Shot;
                if(sample.Composition.Active&&sample.Composition.Subject!=shot.Subject)throw new ArgumentException("active measure subject disagrees with governing camera shot at frame "+frame);
                rows.Add(new MeasureRow{Frame=frame,Time=time,Camera=sample.Camera,HorizonRoll=HorizonRoll(sample.Camera.Right),Composition=sample.Composition,
                    Shot=new MeasureShot{ResolvedCue=active.Value,AuthoredCue=authoredCues[active.Value],T=cue.T,Type=shot.Type,Subject=shot.Subject,From=shot.From}});
            }
            return rows;
        }
        private static bool Finite(double? value)=>value.HasValue&&!double.IsNaN(value.Value)&&!double.IsInfinity(value.Value);
        private static bool Vector(IReadOnlyList<double> values,int count)=>values!=null&&values.Count==count&&values.All(v=>Finite(v));
        private static bool Valid(MeasureSample s)
        {
            var c=s.Camera;var composition=s.Composition;
            return Finite(s.Time)&&c!=null&&Vector(c.Position,3)&&Vector(c.Right,3)&&Vector(c.Up,3)&&Vector(c.Forward,3)&&Finite(c.Fov)&&Finite(c.Aspect)&&CompositionReview.IsValid(composition)&&
                (!composition.Active||!string.IsNullOrEmpty(composition.Subject)&&composition.Roles!=null);
        }
        public static double HorizonRoll(IReadOnlyList<double> right)
        {
            if(right==null||right.Count!=3)throw new ArgumentException("camera right needs three finite coordinates");
            foreach(var v in right)if(double.IsNaN(v)||double.IsInfinity(v))throw new ArgumentException("camera right needs three finite coordinates");
            return Math.Asin(Math.Max(-1,Math.Min(1,right[1])))*180/Math.PI;
        }
    }
}
