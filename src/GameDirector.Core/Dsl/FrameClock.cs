using System;

namespace GameDirector.Core.Dsl
{
    /// <summary>One seconds/frames conversion for capture and editing. Frame i
    /// is always sampled at i/fps, never an accumulated sum of frame steps.</summary>
    public static class FrameClock
    {
        /// <summary>Inclusive tolerance in FRAMES, independent of frame rate.
        /// Alignment and ceiling-based counts absorb this much boundary noise;
        /// a genuinely fractional frame outside it rounds up. Seconds-based
        /// range checks use AlignmentToleranceFrames/fps, not another epsilon.</summary>
        public const double AlignmentToleranceFrames = 1e-5;
        /// <summary>Cue dispatch tolerance in SECONDS (not a frame tolerance).
        /// A cue this close to a target may fire there without advancing past it.</summary>
        public const double CueEpsilonSeconds = 1e-9;

        public static double TimeOf(int frame, int fps)
        {
            RequireRate(fps);
            if(frame<0)throw new ArgumentOutOfRangeException(nameof(frame));
            return (double)frame/fps;
        }
        public static int FrameCount(double duration, int fps)
        {
            RequireRate(fps);RequireTime(duration);
            // Even a sub-tolerance positive take needs its frame at t=0.
            return Math.Max(duration>0?1:0,checked((int)Math.Ceiling(duration*fps-AlignmentToleranceFrames)));
        }
        /// <summary>First sampled frame whose cue-dispatch clock shows this cue; not a duration ceiling.</summary>
        public static int FirstShowingCue(double time,int fps)
        {
            var frame=FrameCount(time,fps);
            while(frame>0&&TimeOf(frame-1,fps)+CueEpsilonSeconds>=time)frame--;
            while(TimeOf(frame,fps)+CueEpsilonSeconds<time)frame=checked(frame+1);
            return frame;
        }
        /// <summary>Index of an aligned instant, not the ceiling of a duration.
        /// Boundary noise is snapped only after the alignment gate accepts it.</summary>
        public static int FrameIndex(double alignedTime, int fps)
        {
            RequireRate(fps);RequireTime(alignedTime);
            if(!IsAligned(alignedTime,fps))throw new ArgumentException("time must align to a frame",nameof(alignedTime));
            return checked((int)Math.Round(alignedTime*fps));
        }
        public static bool IsAligned(double t, int fps)
        {
            RequireRate(fps);
            return t>=0 && !double.IsInfinity(t) && Math.Abs(t*fps-Math.Round(t*fps))<=AlignmentToleranceFrames;
        }
        /// <summary>Nearest frame boundary, with midpoint ties to even. Callers
        /// validating authored boundaries must check IsAligned before snapping.</summary>
        public static double Snap(double t, int fps)
        {
            RequireRate(fps);RequireTime(t);
            return Math.Round(t*fps)/fps;
        }
        private static void RequireRate(int fps) { if(fps<=0)throw new ArgumentOutOfRangeException(nameof(fps)); }
        private static void RequireTime(double t) { if(t<0 || double.IsNaN(t) || double.IsInfinity(t))throw new ArgumentOutOfRangeException(nameof(t)); }
    }
}
