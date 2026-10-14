using GameDirector.Client;
using Xunit;
namespace GameDirector.Core.Tests;

// xUnit 2.7 has discovery-time Skip, not supported runtime SkipException
// handling in the v2 runner. Encoder tests must be reported as skipped when
// either tool is absent; pure receipt validation never uses this attribute.
public sealed class MediaTheoryAttribute : TheoryAttribute
{
    public MediaTheoryAttribute()
    {
        try {
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
                MediaTools.Run(tool, new[] { "-version" }).GetAwaiter().GetResult();
        } catch (Exception ex) { Skip = "ffmpeg/ffprobe unavailable: " + ex.Message; }
    }
}
