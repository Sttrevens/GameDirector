using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    public sealed class TakeRequest
    {
        public TimelineAsset Timeline { get; set; }
        public string SourceFingerprint { get; set; }
        public int FrameRate { get; set; } = 24;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
    }
    public sealed class FrameRequest
    {
        public string TakeId { get; set; }
        public int FrameIndex { get; set; }
    }
    public sealed class TakeReceipt
    {
        public string TakeId { get; set; }
        public string TimelineId { get; set; }
        public string State { get; set; }
        public int FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FrameCount { get; set; }
        public int CapturedFrames { get; set; }
        public string Error { get; set; }
        public string Audio { get; set; } = "silent-picture; mix audio in the edit";
    }
}
