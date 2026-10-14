using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public class ManifestProtocolTests
{
    [Theory] [InlineData("version")] [InlineData("duplicate")] [InlineData("capability")]
    public void FilmPreparationDoesNotRewriteEngineManifestErrorsAsCallerCoordinates(string kind)
    {
        var manifest = TestAssets.InvalidManifest(kind);
        manifest.Capabilities[CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox;
        manifest.Capabilities[CapabilityKeys.PresentationSourceFingerprint] = "fixture";
        var film = FilmCompiler.Starter(manifest);
        var ex = Assert.Throws<EngineProtocolException>(() => FilmCompiler.Prepare(film, manifest));
        Assert.Contains("manifest", ex.Message);
        Assert.DoesNotContain("scenes[", ex.Message);
    }

    [Fact]
    public void PublicManifestVerdictHasNoDependencyOnATimeline()
    {
        Assert.False(TimelineCompiler.ValidateManifest(TestAssets.Manifest()).HasErrors);
        var invalid = TimelineCompiler.ValidateManifest(null!);
        Assert.True(invalid.HasErrors);
        Assert.Equal("manifest", Assert.Single(invalid.Diagnostics).Path);
    }
}
