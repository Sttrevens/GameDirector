using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>
/// The checked-in sample timeline and the Stardeck manifest are part of the
/// public contract: they must always (a) parse, (b) compile error-free.
/// If this test breaks, either the DSL changed without a version bump or the
/// sample assets drifted — both are release blockers by design.
/// Every authored sample parses through the STRICT authored loader, the same
/// path the CLI and MCP use, so a misspelled field in a sample fails here.
/// </summary>
public sealed class SampleAssetTests
{
    private static string SamplePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", file);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameDirector.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root containing GameDirector.sln");
    }

    [Fact]
    public void Stardeck_manifest_parses_and_declares_minimum_vocabulary()
    {
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(SamplePath("stardeck.manifest.json"));
        Assert.Equal("Stardeck", manifest.Game);
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
    public void Sample_timeline_compiles_against_stardeck_manifest_with_zero_errors()
    {
        var timeline = DslJson.LoadAuthored<TimelineAsset>(SamplePath("stardeck.v1.json"));
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(SamplePath("stardeck.manifest.json"));
        var result = TimelineCompiler.Compile(timeline, manifest);

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        Assert.NotNull(result.Timeline);
        Assert.True(result.Timeline!.Duration > 0);
    }

    [Theory]
    [InlineData("sandring.timeline.json")]
    [InlineData("sandring.scout.json")]
    public void Sandring_samples_compile_against_the_cut_only_manifest_with_zero_errors(string file)
    {
        // The sandring adapter declares actor.animation.fade: cut-only and
        // actor.spawn: unsupported; its checked-in samples respect both.
        var timeline = DslJson.LoadAuthored<TimelineAsset>(SamplePath(file));
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(SamplePath("sandring.manifest.json"));
        var result = TimelineCompiler.Compile(timeline, manifest);

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        Assert.NotNull(result.Timeline);
    }

    [Theory]
    [InlineData("v1.json", 960, 540)]
    [InlineData("v1.json", 540, 960)]
    [InlineData("v2.json", 960, 540)]
    [InlineData("v2.json", 540, 960)]
    public void Stardeck_revisions_admit_at_both_aspects(string revision, int width, int height)
    {
        var root = RepoRoot();
        var timeline = DslJson.LoadAuthored<TimelineAsset>(Path.Combine(root, "samples/timelines/stardeck", revision));
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(Path.Combine(root, "adapters/stardeck/manifest.json"));
        var admission = TakeAdmission.Admit(timeline, manifest, 24, width, height);
        Assert.True(admission.Ok, string.Join("; ", admission.Errors));
        Assert.Equal(2, manifest.Roles.Count); Assert.Equal(2, manifest.Actors.Count);
        Assert.All(manifest.Actors, actor => Assert.Equal(17, actor.Clips.Count));
        Assert.Equal(24, manifest.Locations.Count);
    }

    [Fact]
    public void Godot_expect_sidecar_is_classified_as_expect_with_both_sizes()
    {
        var expect = DslJson.LoadAuthored<ExpectFile>(Path.Combine(RepoRoot(), "samples/timelines/stardeck/v2.godot.expect.json"));
        Assert.Equal("v2.json", expect.Timeline);
        Assert.Equal(new[] { "540x960", "960x540" }, expect.Sizes.Select(s => s.Width + "x" + s.Height).OrderBy(s => s).ToArray());
        Assert.All(expect.Sizes, size => Assert.NotEmpty(size.Rows));
    }

    [Fact]
    public void Public_contract_sample_strict_loads_and_compiles_restored_cue_families()
    {
        var folder = Path.Combine(RepoRoot(), "samples/contract/stardeck-cue-coverage");
        Assert.True(File.Exists(Path.Combine(folder, "timeline.json")), "public authored contract timeline is missing");
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")), "public synthetic contract manifest is missing");
        var timeline = DslJson.LoadAuthored<TimelineAsset>(Path.Combine(folder, "timeline.json"));
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(Path.Combine(folder, "manifest.json"));
        foreach (var type in new[] { CueTypes.ActorSpawn, CueTypes.AudioPlay, CueTypes.AudioStop, CueTypes.WorldTimeScale })
            Assert.Contains(timeline.Cues, cue => cue.Type == type);
        Assert.Equal("synthetic-local", manifest.Capabilities["fixture.networking"]);
        var result = TimelineCompiler.Compile(timeline, manifest);
        Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics));
        Assert.NotNull(result.Timeline);
        Assert.Contains(result.Timeline!.OrderedCues, c => c.Type == CueTypes.WorldTimeScale && c.Injected && c.Scale == 1);
    }

    [Fact]
    public void Public_stardeck_edit_strict_loads_and_validates_over_compiled_v1_v2_takes()
    {
        var root = RepoRoot(); var folder = Path.Combine(root, "samples/timelines/stardeck");
        Assert.True(File.Exists(Path.Combine(folder, "edit.json")), "public authored edit plan is missing");
        var edit = DslJson.LoadAuthored<EditPlan>(Path.Combine(folder, "edit.json"));
        Assert.Equal(new[] { "v1", "v2" }, edit.Sources.Keys.OrderBy(k => k));
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(Path.Combine(root, "adapters/stardeck/manifest.json"));
        var durations = new Dictionary<string, double>();
        foreach (var revision in new[] { "v1", "v2" })
        {
            var timeline = DslJson.LoadAuthored<TimelineAsset>(Path.Combine(folder, revision + ".json"));
            var compiled = TimelineCompiler.Compile(timeline, manifest); Assert.False(compiled.HasErrors);
            durations[revision] = compiled.Timeline!.Duration;
            Assert.Contains(edit.Ranges, range => range.Source == revision);
        }
        EditRenderer.Validate(edit, durations);
    }

    [Fact]
    public void Every_authored_sample_loads_through_the_strict_loader()
    {
        // The samples are the authoring contract's evidence: each must survive the
        // same strict load the CLI/MCP apply, or an author copying them inherits
        // the failure. Non-DSL helper files (build-script inputs, package
        // metadata) are listed explicitly so a new sample file must choose a side.
        var root = RepoRoot();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "adapters/sandring/package.json",                        // npm package metadata
            "samples/godot/stardeck/addons/gamedirector/bridge_defaults.json", // engine-local addon config; native dock + CLI help tests consume it
        };
        var loaded = new List<string>();
        foreach (var folder in new[] { "samples", "adapters" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.json", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (skipped.Contains(relative) || relative.Contains("/.godot/", StringComparison.Ordinal)) continue;
                switch (relative)
                {
                    case var r when r.StartsWith("adapters/") && r.EndsWith("/three.json"):
                        // Adapter-only engine-local config, validated by packages/gamedirector-three/bundle.mjs
                        // and tools/test_three_bundle.mjs; synthetic stage in tools/fixtures/three-ts-adapter.
                        continue;
                    case var r when r.EndsWith("/edit.json"):
                        DslJson.LoadAuthored<EditPlan>(file); break;
                    case var r when r.EndsWith("/performances.json"):
                        DslJson.LoadAuthored<PerformanceCatalog>(file); break;
                    case var r when r.EndsWith(".manifest.json") || r.EndsWith("/manifest.json"):
                        DslJson.LoadAuthored<CapabilityManifest>(file); break;
                    case var r when r.StartsWith("samples/films/"):
                        DslJson.LoadAuthored<FilmPlan>(file); break;
                    case var r when r.EndsWith(".expect.json"):
                        DslJson.LoadAuthored<ExpectFile>(file); break;
                    case var r when r.StartsWith("samples/timelines/") || r.StartsWith("samples/contract/"):
                        DslJson.LoadAuthored<TimelineAsset>(file); break;
                    default:
                        throw new InvalidOperationException("unclassified sample file: " + relative);
                }
                loaded.Add(relative);
            }
        }
        // Guard against the enumeration silently shrinking to nothing.
        Assert.True(loaded.Count >= 12, "expected every sample asset including Stardeck manifest/v1/v2, loaded: " + string.Join(", ", loaded));
    }
}
