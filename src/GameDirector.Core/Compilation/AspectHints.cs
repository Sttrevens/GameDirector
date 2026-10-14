using System;
using System.Collections.Generic;
using GameDirector.Core.Dsl;
namespace GameDirector.Core.Compilation
{
    /// <summary>Core authoring policy, not an adapter capability. Resolution never mutates inputs;
    /// clients store/send its output so even an old package cannot play both alternates.</summary>
    public static class AspectHints
    {
        public static bool HasHints(TimelineAsset asset) => asset?.Cues != null && asset.Cues.Exists(c => c?.Aspect != null);
        internal static bool Matches(Cue cue,double aspect) => cue.Aspect == null ||
            aspect >= (cue.Aspect.Min ?? 0) && aspect < (cue.Aspect.Max ?? double.PositiveInfinity);
        public static TimelineAsset Resolve(TimelineAsset asset,double? aspect = null)=>Resolve(asset,aspect,out _);
        /// <summary>Index i of the resolved cues maps to AuthoredCues[i]; no DSL/bookkeeping field is repurposed.</summary>
        public static TimelineAsset Resolve(TimelineAsset asset,double? aspect,out IReadOnlyList<int> authoredCues)
        {
            var mapping=new List<int>();authoredCues=mapping;
            if (!HasHints(asset)) { for(int i=0;i<(asset?.Cues?.Count??0);i++)mapping.Add(i);return asset; }
            var result = new CompileResult(); Validate(asset,aspect,result);
            if (result.HasErrors) throw new ArgumentException(string.Join("\n",result.Diagnostics));
            var cues = new List<Cue>();
            for(int i=0;i<asset.Cues.Count;i++) { var cue=asset.Cues[i];
                if (cue != null && !Matches(cue,aspect.Value)) continue;
                var copy = cue?.Snapshot(); if (copy != null) copy.Aspect = null; cues.Add(copy);mapping.Add(i);
            }
            return new TimelineAsset { Version=asset.Version,Id=asset.Id,Title=asset.Title,Cues=cues,
                Metadata=asset.Metadata==null?null:new Dictionary<string,string>(asset.Metadata) };
        }
        internal static void Validate(TimelineAsset asset,double? aspect,CompileResult result)
        {
            if (!HasHints(asset)) return;
            var cues = asset.Cues; var groups = new Dictionary<double,List<int>>();
            for (int i=0;i<cues.Count;i++) {
                var cue=cues[i]; if (cue?.Aspect==null) continue;
                if (!aspect.HasValue || !Finite(aspect.Value) || aspect.Value<=0)
                    Error(result,i,"aspect","hinted timelines require an explicit capture width and height (both positive)");
                if (cue.Type!=CueTypes.CameraShot) { Error(result,i,"aspect","aspect is allowed only on camera.shot"); continue; }
                if (!Finite(cue.T) || cue.T<0) continue; // the compiler owns cue time diagnostics
                if (!groups.TryGetValue(cue.T,out var group)) groups.Add(cue.T,group=new List<int>());group.Add(i);
                var min=cue.Aspect.Min??0;var max=cue.Aspect.Max??double.PositiveInfinity;
                if (!Finite(min) || min<0 || cue.Aspect.Max.HasValue && !Finite(max) || max<=min)
                    Error(result,i,"aspect","aspect bounds must be finite and nonnegative with min < max (omitted max is unbounded)");
            }
            foreach (var pair in groups) {
                var group=pair.Value;var first=group[0];
                if (group[group.Count-1]-first+1!=group.Count) Error(result,first,"aspect","group alternates must be contiguous in authored order");
                foreach (var i in group) {
                    if (cues[i].Shot?.DurationSeconds!=cues[first].Shot?.DurationSeconds)
                        Error(result,i,"shot.durationSeconds","aspect alternates must share durationSeconds");
                    if (cues[i].Role!=cues[first].Role) Error(result,i,"role","aspect alternates must share their event role");
                    if (cues[i].Label!=cues[first].Label) Error(result,i,"label","aspect alternates must share their event label");
                }
                for (int i=0;i<cues.Count;i++) if (cues[i]?.Type==CueTypes.CameraShot && cues[i].T==pair.Key && cues[i].Aspect==null)
                    Error(result,i,"aspect","an unhinted camera cue cannot share an aspect group's t");
                var ranges=new List<int>(group);ranges.Sort((a,b)=>(cues[a].Aspect.Min??0).CompareTo(cues[b].Aspect.Min??0));
                double edge=0;bool partitions=true;
                foreach (var i in ranges) { if ((cues[i].Aspect.Min??0)!=edge) partitions=false;edge=cues[i].Aspect.Max??double.PositiveInfinity; }
                if (!partitions || !double.IsPositiveInfinity(edge)) Error(result,first,"aspect","group intervals must partition (0, infinity), without gaps or overlaps");
            }
            // An explicit-anchor cue only resets the live pose after time can advance.
            // Same-instant reads (including the cue epsilon) still depend on the hinted pose.
            var cameras=new List<int>();for (int i=0;i<cues.Count;i++) if (cues[i]?.Type==CueTypes.CameraShot) cameras.Add(i);
            cameras.Sort((a,b)=>{var order=cues[a].T.CompareTo(cues[b].T);return order==0?a.CompareTo(b):order;});
            bool dependent=false;double? resetT=null;
            for (int p=0;p<cameras.Count;p++) {
                var index=cameras[p];var cue=cues[index];int end=p;
                if (dependent && resetT.HasValue && cue.T > resetT.Value + FrameClock.CueEpsilonSeconds) { dependent=false;resetT=null; }
                if (cue.Aspect!=null) while (end+1<cameras.Count && cues[cameras[end+1]].T==cue.T && cues[cameras[end+1]].Aspect!=null) end++;
                for (int q=p;q<=end;q++) {
                    var i=cameras[q];var s=cues[i].Shot;if (s==null) continue;
                    if (dependent && (s.From==ShotVocabulary.CurrentAnchor || s.To==ShotVocabulary.CurrentAnchor))
                        Error(result,i,s.From==ShotVocabulary.CurrentAnchor?"shot.from":"shot.to","current after a hinted group requires an unhinted explicit-anchor camera cue strictly earlier than the read, with a live subject/lookAt through its instant");
                }
                if (cue.Aspect!=null) { dependent=true;resetT=null; }
                else if (dependent && cue.Shot!=null && cue.Shot.From!=ShotVocabulary.CurrentAnchor && cue.Shot.To!=ShotVocabulary.CurrentAnchor) {
                    // Despawn clears the active shot before it can take the explicit pose.
                    bool clears=cues.Exists(c=>c?.Type==CueTypes.ActorDespawn && Math.Abs(c.T-cue.T)<=FrameClock.CueEpsilonSeconds &&
                        (c.Role==cue.Shot.Subject || c.Role==cue.Shot.LookAt));
                    resetT=clears?(double?)null:cue.T;
                }
                p=end;
            }
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void Error(CompileResult result,int index,string field,string message) => result.Diagnostics.Add(new Diagnostic {
            Severity=DiagnosticSeverity.Error,Code=TimelineCompiler.EAspectHint,CueIndex=index,Path="cues["+index+"]."+field,Message=message });
    }
}
