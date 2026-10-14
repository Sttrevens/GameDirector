using System;
using System.Collections.Generic;
using System.Linq;
using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    public static class ExpectFrames
    {
        public static IReadOnlyList<string> AutoLabels{get;}=new[]{"first","mid","last"};
        public static IReadOnlyList<ExpectFrame> Select(TimelineAsset resolved,double duration,int fps,IReadOnlyList<ExpectBeat> beats)
        {
            var error=TakeAdmission.DurationError(duration);if(error!=null)throw new ArgumentException(error);
            CheckBeats(beats);var count=FrameClock.FrameCount(duration,fps);
            var starts=resolved.Cues.Where(c=>c?.Type==CueTypes.CameraShot).Select(c=>FrameClock.FirstShowingCue(c.T,fps)).Where(f=>f<count).Distinct().OrderBy(f=>f).ToArray();
            if(starts.Length==0)throw new ArgumentException("cannot measure without a showing camera shot");
            var frames=new SortedDictionary<int,(List<string> Auto,List<string> Beat)>();
            void Add(int frame,string auto=null,string beat=null){
                if(frame<starts[0])throw new ArgumentException("cannot measure before first camera shot frame "+starts[0]);
                if(frame>=count)throw new ArgumentException("beat frame "+frame+" is outside timeline; last frame index is "+(count-1));
                if(!frames.TryGetValue(frame,out var labels)){labels=(new List<string>(),new List<string>());frames.Add(frame,labels);}
                if(auto!=null&&!labels.Auto.Contains(auto))labels.Auto.Add(auto);if(beat!=null)labels.Beat.Add(beat);
            }
            for(int i=0;i<starts.Length;i++){
                var first=starts[i];var last=i+1<starts.Length?starts[i+1]-1:count-1;
                if(!ActiveShot.CueIndex(resolved,FrameClock.TimeOf(first,fps)).HasValue)throw new ArgumentException("no governing camera shot at frame "+first);
                Add(first,AutoLabels[0]);Add(first+(last-first)/2,AutoLabels[1]);Add(last,AutoLabels[2]);
            }
            foreach(var beat in beats.OrderBy(b=>b.Frame).ThenBy(b=>b.Label,StringComparer.Ordinal))Add(beat.Frame.Value,beat:beat.Label);
            return frames.Select(pair=>new ExpectFrame{Frame=pair.Key,Auto=pair.Value.Auto.Count==0?null:pair.Value.Auto.ToArray(),Beat=pair.Value.Beat.Count==0?null:pair.Value.Beat.ToArray()}).ToArray();
        }
        public static void CheckBeats(IReadOnlyList<ExpectBeat> beats)
        {
            if(beats==null||beats.Any(b=>b==null||string.IsNullOrWhiteSpace(b.Label)||!b.Frame.HasValue||b.Frame<0)||beats.Select(b=>b.Label).Distinct(StringComparer.Ordinal).Count()!=beats.Count)
                throw new ArgumentException("beats need unique nonempty labels and nonnegative frame indices");
        }
    }
}
