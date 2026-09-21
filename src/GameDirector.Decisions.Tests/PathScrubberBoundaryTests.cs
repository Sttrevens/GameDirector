using System.Text.Json;
using GameDirector.Decisions;
using Xunit;

namespace GameDirector.Decisions.Tests;

public class PathScrubberBoundaryTests
{
    [Theory]
    [InlineData("/tmp/scout/frame.png")]
    [InlineData("/var/data/take.mov")]
    [InlineData("file:///private/scout.mov")]
    public void NonHomeFilesystemLocationsAreNotProviderEvidence(string path)
    {
        var text = LocalPathScrubber.Scrub("Observed in " + path);
        Assert.DoesNotContain(path, text);
        Assert.Null(LocalPathScrubber.FindPathLike(JsonSerializer.Serialize(new { text })));
    }

    [Fact]
    public void JsonEscapedKnownRootWithSpacesIsFullyRedacted()
    {
        var root = @"C:\Private Project\Assets";
        var text = LocalPathScrubber.Scrub(JsonSerializer.Serialize(new { evidence = root + @"\scout.mov" }), root);
        Assert.DoesNotContain("Private Project", text);
        Assert.Null(LocalPathScrubber.FindPathLike(text));
    }

    [Fact]
    public void HttpsEvidenceLinksRemainUsable()
    {
        const string url = "https://example.test/observations/take-1";
        Assert.Equal(url, LocalPathScrubber.Scrub(url));
        Assert.Null(LocalPathScrubber.FindPathLike(JsonSerializer.Serialize(url)));
    }
}
