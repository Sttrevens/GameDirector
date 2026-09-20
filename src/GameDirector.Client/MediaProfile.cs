using System;
using System.Collections.Generic;
using System.IO;

namespace GameDirector.Client;

/// <summary>Bundled subtitle typeface. The family name is stated once, next to
/// the file and its pinned hash; rendering and readiness both read it from here,
/// and a corrupted or incomplete install fails the font check, not the frame.</summary>
public sealed record SubtitleFontDescriptor(string FileName, string FamilyName, string Sha256)
{
    public string Locate() => Path.Combine(AppContext.BaseDirectory, "Fonts", FileName);
}

/// <summary>Every encoding decision of the picture pipeline in one injectable
/// object: codec, rate control, sample rate, fades, limiter, thumbnail policy.
/// Recorders and renderers take a profile; nothing in the pipeline restates a
/// codec name, a sample rate or a fade duration. Tooling that wants HEVC or a
/// hardware encoder supplies a different profile instead of editing call sites.</summary>
public sealed record MediaProfile
{
    public string VideoCodec { get; init; } = "libx264";
    public string Preset { get; init; } = "fast";
    public int Crf { get; init; } = 17;
    public string PixelFormat { get; init; } = "yuv420p";
    public int AudioSampleRate { get; init; } = 48000;
    public int AudioChannels { get; init; } = 2;
    public string SegmentAudioCodec { get; init; } = "pcm_s16le";
    public string FinalAudioCodec { get; init; } = "aac";
    public string FinalAudioBitrate { get; init; } = "256k";
    public double LimiterLevel { get; init; } = 0.95;
    public double SegmentFadeSeconds { get; init; } = 0.03;
    public double MusicFadeInSeconds { get; init; } = 0.25;
    public double MusicFadeOutSeconds { get; init; } = 1.5;
    public int ThumbnailWidth { get; init; } = 320;
    public int ThumbnailMax { get; init; } = 20;
    public int ThumbnailColumns { get; init; } = 4;
    public string MuxerFlags { get; init; } = "+faststart";
    public SubtitleFontDescriptor SubtitleFont { get; init; } = DefaultFont;

    // Noto Sans CJK SC (OFL). The hash is pinned here and verified on every
    // subtitled render; it must match src/GameDirector.Client/Fonts/.
    public static readonly SubtitleFontDescriptor DefaultFont = new(
        "NotoSansCJKsc-Regular.otf", "Noto Sans CJK SC",
        "2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b");

    public static MediaProfile Default { get; } = new();

    /// <summary>FFmpeg capabilities this profile needs from the local tool:
    /// the named encoder, plus the subtitle filter when captions are burned.</summary>
    public IReadOnlyList<string> RequiredFilters(bool subtitles) =>
        subtitles ? new[] { "subtitles" } : Array.Empty<string>();
}
