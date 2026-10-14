using System;
using System.Linq;
namespace GameDirector.Core.Capture
{
    // Public review policy data, deliberately NOT an EngineContract member.
    public sealed class CompositionPolicy
    {
        public CompositionPolicy(double edgeTolerance=.005,double safeSideTolerance=.01,double singleSideFraction=.25)
        {
            if(new[]{edgeTolerance,safeSideTolerance,singleSideFraction}.Any(v=>double.IsNaN(v)||double.IsInfinity(v)||v<0))throw new ArgumentException("review policy needs finite nonnegative thresholds");
            EdgeTolerance=edgeTolerance;SafeSideTolerance=safeSideTolerance;SingleSideFraction=singleSideFraction;
        }
        public double EdgeTolerance{get;} public double SafeSideTolerance{get;} public double SingleSideFraction{get;}
        public static CompositionPolicy Default{get;}=new CompositionPolicy();
    }
    public static class CompositionCodes
    {
        public const string Lost="lost",SideCut="side-cut",HeadCut="head-cut",Unsafe="unsafe",Legacy="legacy",BoundsError="bounds-error",NoReviews="no-reviews",MalformedSample="malformed-sample";
    }
    public static class CompositionSeverities{public const string Warning="warning",Error="error";}
}
