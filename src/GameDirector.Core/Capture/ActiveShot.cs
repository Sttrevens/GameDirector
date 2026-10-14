using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    /// <summary>One stable authored-order lookup in the stored resolved timeline; index is never a compiled ordinal.</summary>
    public static class ActiveShot
    {
        public static int? CueIndex(TimelineAsset timeline,double time)
        {
            if(timeline?.Cues==null)return null;
            int? active=null;double latest=double.NegativeInfinity;
            for(int i=0;i<timeline.Cues.Count;i++) {
                var cue=timeline.Cues[i];
                if(cue?.Type==CueTypes.CameraShot&&cue.T<=time+FrameClock.CueEpsilonSeconds&&cue.T>=latest){active=i;latest=cue.T;}
            }
            return active;
        }
    }
}
