using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public sealed class TakeAdmissionTests
{
    private static TimelineAsset Take(string id = "t_take", double shotDuration = 2.5) => new TimelineAsset
    {
        Id = id,
        Cues = new List<Cue> { TestAssets.Shot(0, duration: shotDuration) }
    };

    [Fact]
    public void Admissible_take_carries_compiler_result_and_frame_count()
    {
        var result = TakeAdmission.Admit(Take(), TestAssets.Manifest(), 24, 1280, 720);
        Assert.True(result.Ok);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Compiled.Timeline);
        Assert.Equal(60, result.FrameCount); // ceil(2.5 * 24 - FrameClock.AlignmentToleranceFrames)
    }

    // Moving manifest diagnostics back into caller Errors must fail this test,
    // even when the caller geometry is independently invalid too.
    [Theory] [InlineData("version")] [InlineData("duplicate")] [InlineData("capability")] [InlineData("null-params")] [InlineData("null")]
    public void Invalid_engine_manifest_never_populates_caller_errors(string kind)
    {
        var manifest = kind == "null" ? null : TestAssets.InvalidManifest(kind);
        foreach (var fps in new[] { 24, 0 })
        {
            var result = TakeAdmission.Admit(Take(), manifest!, fps, 1280, 720);
            Assert.False(result.Ok);
            Assert.True(result.ManifestValidation.HasErrors);
            Assert.NotEmpty(result.ManifestValidation.Diagnostics);
            Assert.Empty(result.Errors);
            Assert.Empty(result.GateErrors);
            Assert.Null(result.Compiled);
            Assert.Equal(0, result.FrameCount);
        }
    }

    [Fact]
    public void Frame_count_is_the_single_ceiling_formula()
    {
        Assert.Equal(24, CaptureContract.FrameCount(1.0, 24));
        Assert.Equal(60, CaptureContract.FrameCount(2.5, 24));
        Assert.Equal(3, CaptureContract.FrameCount(0.5, 6));
        Assert.Equal(8, CaptureContract.FrameCount(1.0 / 3, 24));      // 7.999… must round up
        Assert.Equal(27, CaptureContract.FrameCount(1.1, 24));
        Assert.Equal(24, CaptureContract.FrameCount(1.0 + 2e-10, 24)); // float noise on a boundary is absorbed
        Assert.Equal(1, CaptureContract.FrameCount(1.0 / 24, 24));
    }

    [Fact]
    public void Geometry_is_checked_before_any_frame()
    {
        foreach (var (fps, w, h) in new[] { (0, 1280, 720), (61, 1280, 720), (24, 63, 720), (24, 1281, 720), (24, 1280, 2161) })
        {
            var result = TakeAdmission.Admit(Take(), TestAssets.Manifest(), fps, w, h);
            Assert.False(result.Ok);
            Assert.Equal(0, result.FrameCount);
            Assert.Contains(result.Errors, e => e.Contains("frame rate") || e.Contains("dimensions"));
        }
    }

    [Fact]
    public void Compile_errors_are_admission_errors()
    {
        var asset = Take();
        asset.Cues[0].Shot.Subject = "ghost";
        var result = TakeAdmission.Admit(asset, TestAssets.Manifest(), 24, 1280, 720);
        Assert.False(result.Ok);
        Assert.Null(result.Compiled.Timeline);
        Assert.Contains(result.Errors, e => e.Contains("GD1001"));
        Assert.Equal(0, result.FrameCount);
    }

    [Fact]
    public void A_picture_take_needs_a_camera_shot_at_t_zero()
    {
        var asset = Take();
        asset.Cues[0].T = 1;
        var result = TakeAdmission.Admit(asset, TestAssets.Manifest(), 24, 1280, 720);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("first captured frame"));
    }

    [Theory] [InlineData(.9, true)] [InlineData(1, true)] [InlineData(1.000001, false)]
    public void Camera_must_be_ready_at_the_first_captured_frame_not_the_next_step(double cameraTime, bool ok)
    {
        var asset = Take(shotDuration: 2); asset.Cues[0].T = cameraTime;
        var result = TakeAdmission.Admit(asset, TestAssets.Manifest(), 6, 64, 64, 6);
        Assert.Equal(ok, result.Ok);
        if (ok) Assert.Equal(18, result.FrameCount); // duration still includes replay
        else Assert.Contains(result.Errors, e => e.Contains("first captured frame"));
    }

    [Theory] [InlineData(-1, false)] [InlineData(0, true)] [InlineData(14, true)] [InlineData(15, false)] [InlineData(16, false)]
    public void Capture_start_is_in_the_half_open_timeline_frame_range(int from, bool ok)
    {
        var result = TakeAdmission.Admit(Take(), TestAssets.Manifest(), 6, 64, 64, from);
        Assert.Equal(ok, result.Ok);
        if (ok) Assert.Equal(15, result.FrameCount);
        else { Assert.Equal(0, result.FrameCount); Assert.Contains(result.Errors, e => e.Contains("CaptureFromFrame")); }
    }

    [Fact]
    public void Duration_must_be_positive_and_within_the_take_ceiling()
    {
        var empty = new TimelineAsset { Id = "t_zero", Cues = new List<Cue> { new Cue { T = 0, Type = CueTypes.Marker, Label = "only" } } };
        Assert.Contains(TakeAdmission.Admit(empty, TestAssets.Manifest(), 24, 1280, 720).Errors, e => e.Contains("duration"));

        var longTake = Take();
        longTake.Cues.Add(new Cue { T = CaptureContract.MaxTakeSeconds + 1, Type = CueTypes.Marker, Label = "late" });
        Assert.Contains(TakeAdmission.Admit(longTake, TestAssets.Manifest(), 24, 1280, 720).Errors, e => e.Contains("duration"));

        var atCeiling = Take(shotDuration: CaptureContract.MaxTakeSeconds);
        Assert.True(TakeAdmission.Admit(atCeiling, TestAssets.Manifest(), 24, 1280, 720).Ok);
    }

    [Fact]
    public void Live_audio_cues_are_not_recordable()
    {
        var asset = Take();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.AudioPlay, AudioId = "bgm_dark" });
        var result = TakeAdmission.Admit(asset, TestAssets.Manifest(), 24, 1280, 720);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("audio"));
        // The same cue is legal for interactive playback: compile alone passes.
        Assert.False(result.Compiled.HasErrors);
    }

    [Fact]
    public void Fractional_durations_round_up_through_admit()
    {
        var result = TakeAdmission.Admit(Take(shotDuration: 1.0 / 3), TestAssets.Manifest(), 24, 64, 64);
        Assert.True(result.Ok);
        Assert.Equal(8, result.FrameCount);
    }

    [Fact]
    public void Admission_is_a_pure_function_of_its_inputs()
    {
        var a = TakeAdmission.Admit(Take(), TestAssets.Manifest(), 24, 1280, 720);
        var b = TakeAdmission.Admit(Take(), TestAssets.Manifest(), 24, 1280, 720);
        Assert.Equal(a.FrameCount, b.FrameCount);
        Assert.Equal(a.Errors, b.Errors);
    }
}
