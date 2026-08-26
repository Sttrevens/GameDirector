using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public sealed class TimelineCompilerTests
{
    private static CompileResult Compile(TimelineAsset asset) => TimelineCompiler.Compile(asset, TestAssets.Manifest());

    [Fact]
    public void Valid_timeline_compiles_with_zero_errors()
    {
        var result = Compile(TestAssets.ValidTimeline());
        Assert.False(result.HasErrors);
        Assert.NotNull(result.Timeline);
        Assert.Equal("t_valid", result.Timeline!.Id);
    }

    [Fact]
    public void Compiled_cues_are_sorted_by_time_then_source_order()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Reverse(); // authored order must not matter for timing
        var result = Compile(asset);
        var cues = result.Timeline!.OrderedCues;
        for (int i = 1; i < cues.Count; i++)
            Assert.True(cues[i].T >= cues[i - 1].T, $"cues out of order at {i}");
    }

    [Fact]
    public void Timescale_with_duration_injects_explicit_restore_cue()
    {
        var result = Compile(TestAssets.ValidTimeline());
        var injected = result.Timeline!.OrderedCues.Where(c => c.Injected).ToList();
        Assert.Single(injected);
        Assert.Equal(CueTypes.WorldTimeScale, injected[0].Type);
        Assert.Equal(1f, injected[0].Scale);
        Assert.Equal(5.0, injected[0].T, precision: 6); // t=4 + duration=1
    }

    [Fact]
    public void Unknown_role_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.ActorAnim, Role = "ghost", Clip = "Idle" });
        var result = Compile(asset);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownRole);
        Assert.Null(result.Timeline);
    }

    [Fact]
    public void Unknown_clip_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.ActorAnim, Role = "bigguai", Clip = "Moonwalk" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownClip && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Unknown_location_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.ActorSpawn, Role = "bigguai", Location = "loc_nowhere" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownLocation);
    }

    [Fact]
    public void Unknown_shot_type_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.CameraShot, Shot = new ShotSpec { Type = "helicopter", Subject = "hero", Frame = "wide" } });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownShotType);
    }

    [Fact]
    public void Negative_time_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = -0.5, Type = CueTypes.Marker, Label = "bad" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EBadTime);
    }

    [Fact]
    public void Unknown_cue_type_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = "explosion.big" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownCueType);
    }

    [Fact]
    public void Overlapping_shots_warn_but_still_compile()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(TestAssets.Shot(0.5)); // overlaps the opening shot at t=0
        var result = Compile(asset);
        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.WShotOverlap && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Anim_on_not_spawned_role_warns()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Clear();
        asset.Cues.Add(new Cue { T = 0, Type = CueTypes.ActorAnim, Role = "bigguai", Clip = "Idle" }); // PresentAtStart=false, no spawn
        var result = Compile(asset);
        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.WRoleNotPresent);
    }

    [Fact]
    public void Reserved_current_location_is_accepted_for_shot_from()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.CameraShot, Shot = new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "closeup", From = "current" } });
        var result = Compile(asset);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownLocation);
    }
}
