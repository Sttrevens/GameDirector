using System.Collections.Generic;
using GameDirector.Core.Dsl;

namespace GameDirector.Core.Capture
{
    public static class ReviewPolicy
    {
        public const int IntervalSeconds=1;
        /// <summary>Timeline indices, rendered frames only; callers skip advance-only.</summary>
        public static bool IsReviewFrame(int i,int fps)=>i%(fps*IntervalSeconds)==0;
        public static readonly IReadOnlyList<string> Required=new[]{"frame","time","composition.active","composition.subject","composition.center"};
        public static readonly IReadOnlyList<string> Optional=new[]{"composition.shotType","composition.visibleSamples","composition.totalSamples","composition.note","composition.roles"};
        public static readonly IReadOnlyList<CadenceSample> Samples=new[]{new CadenceSample(4,8,0,0,4),new CadenceSample(4,8,2,4),new CadenceSample(1,2,1,1),
            new CadenceSample(6,12,5,6),new CadenceSample(24,49,0,0,24,48),new CadenceSample(24,48,30)};
    }
}
