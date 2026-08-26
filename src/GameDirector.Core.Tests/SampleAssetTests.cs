using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>
/// The checked-in sample timeline and the CDREBIRTH manifest are part of the
/// public contract: they must always (a) parse, (b) compile error-free.
/// If this test breaks, either the DSL changed without a version bump or the
/// sample assets drifted — both are release blockers by design.
/// </summary>
public sealed class SampleAssetTests
{
    private static string SamplePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", file);

    [Fact]
    public void Cdrebirth_manifest_parses_and_declares_minimum_vocabulary()
    {
        var manifest = DslJson.Load<CapabilityManifest>(SamplePath("cdrebirth.manifest.json"));
        Assert.Equal("CDREBIRTH", manifest.Game);
        Assert.NotEmpty(manifest.Roles);
        Assert.NotEmpty(manifest.Actors);
        Assert.NotEmpty(manifest.Locations);
        Assert.NotEmpty(manifest.ShotTypes);
        Assert.NotEmpty(manifest.FrameTypes);

        // Every role's DefaultActor must resolve; dangling refs make LLM output fail late.
        foreach (var role in manifest.Roles)
            Assert.True(manifest.FindActor(role.DefaultActor) != null,
                $"role '{role.Id}' references undeclared actor '{role.DefaultActor}'");
    }

    [Fact]
    public void Sample_timeline_compiles_against_cdrebirth_manifest_with_zero_errors()
    {
        var timeline = DslJson.Load<TimelineAsset>(SamplePath("pv_grimforest_demo.json"));
        var manifest = DslJson.Load<CapabilityManifest>(SamplePath("cdrebirth.manifest.json"));
        var result = TimelineCompiler.Compile(timeline, manifest);

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        Assert.NotNull(result.Timeline);
        Assert.True(result.Timeline!.Duration > 0);
    }
}
