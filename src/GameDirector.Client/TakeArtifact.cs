using GameDirector.Core.Capture;

namespace GameDirector.Client;

/// <summary>A take is an artifact: the verified picture plus this receipt
/// (take.json). Frames are a resumable-capture intermediate, never the product;
/// reuse costs this receipt check and one video hash, not a re-hash of frames.
/// Version stays 1: receipts written before the typed reader still load.</summary>
public sealed record TakeArtifact
{
    public int Version { get; set; } = 1;
    public string? TakeId { get; set; }
    public string? TimelineId { get; set; }
    public string State { get; set; } = "";
    /// <summary>Picture length (captured frames only); timeline indices start at CaptureFromFrame.</summary>
    public int FrameCount { get; set; }
    public int CaptureFromFrame { get; set; }
    public int FrameRate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public string Video { get; set; } = "picture.mp4";
    public string? VideoSha256 { get; set; }
    public string? TimelineSha256 { get; set; }
    public string? ManifestSha256 { get; set; }
    public string Audio { get; set; } = TakeReceipt.SilentPictureAudio;
}

/// <summary>Take-job journal states (job.json). Prepared/Capturing/FramesComplete
/// are capture phases; Interrupted is resumable; Verified is terminal for pixels.</summary>
public static class TakeJobStates
{
    public const string Prepared = "Prepared";
    public const string Capturing = TakeStates.Capturing;
    public const string FramesComplete = "FramesComplete";
    public const string Interrupted = "Interrupted";
    public const string Verified = "Verified";
}
