using System.Collections.Generic;
using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    public sealed class TakeRequest
    {
        public TimelineAsset Timeline { get; set; }
        public string SourceFingerprint { get; set; }
        /// <summary>Replay every timeline frame, but capture only [CaptureFromFrame, FrameCount).
        /// Zero preserves full-take capture.</summary>
        public int CaptureFromFrame { get; set; }
        public int FrameRate { get; set; } = FilmDefaults.FrameRate;
        public int Width { get; set; } = FilmDefaults.Width;
        public int Height { get; set; } = FilmDefaults.Height;
        /// <summary>Structural transport snapshot; the authored timeline is borrowed.</summary>
        public TakeRequest Snapshot() => (TakeRequest)MemberwiseClone();
    }
    public sealed class FrameRequest
    {
        public string TakeId { get; set; }
        /// <summary>Timeline index, including replay-only frames; strictly sequential.</summary>
        public int FrameIndex { get; set; }
        /// <summary>Required before CaptureFromFrame: advance one frame without rendering;
        /// the bridge returns an empty 204. Retrying the last index is a no-op.</summary>
        public bool AdvanceOnly { get; set; }
        /// <summary>Read a measure row after rendering, before advancing; invalid on advance-only frames.</summary>
        public bool Measure { get; set; }
        public FrameRequest Snapshot() => (FrameRequest)MemberwiseClone();
    }
    public sealed class TakeStopRequest
    {
        public string TakeId { get; set; }
        public TakeStopRequest Snapshot() => (TakeStopRequest)MemberwiseClone();
    }

    /// <summary>Take lifecycle states. Engines, recorders and test fixtures
    /// compare against these constants instead of restating the literals.</summary>
    public static class TakeStates
    {
        public const string Capturing = "Capturing";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";
        public static readonly IReadOnlyList<string> All = new[]{Capturing,Completed,Failed,Cancelled};
        public static readonly IReadOnlyDictionary<string,string> Protocol = BuildProtocol();
        private static IReadOnlyDictionary<string,string> BuildProtocol()
        {var map=new Dictionary<string,string>();foreach(var state in All)map.Add(char.ToLowerInvariant(state[0])+state.Substring(1),state);return map;}
    }

    public sealed class TakeReceipt
    {
        /// <summary>Frame-stepped capture never records sound; the receipt says so.</summary>
        public const string SilentPictureAudio = "silent-picture; mix audio in the edit";
        public string TakeId { get; set; }
        public string TimelineId { get; set; }
        public string State { get; set; }
        public int FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>Total timeline frames to replay, not the picture length.</summary>
        public int FrameCount { get; set; }
        /// <summary>Echoed capture start; picture length is FrameCount - CaptureFromFrame.</summary>
        public int CaptureFromFrame { get; set; }
        /// <summary>Number of rendered frames so far, excluding replay-only steps.</summary>
        public int CapturedFrames { get; set; }
        public string Error { get; set; }
        public string Audio { get; set; } = SilentPictureAudio;
    }
}
