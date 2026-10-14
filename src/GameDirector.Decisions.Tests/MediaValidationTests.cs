using GameDirector.Client;
using GameDirector.Workbench;
using Xunit;

namespace GameDirector.Decisions.Tests;

/// <summary>One overrun bound, shared by import validation and the edit
/// renderer. Work the edit would refuse must be refused at validation, before
/// any frame is captured — never only at the final edit.</summary>
public class MediaValidationTests
{
    [Fact]
    public async Task AudioOverrunOutsideBoundaryNoiseIsRefusedAtValidationTime()
    {
        await using var rig = await ServiceRig.Start(null);
        var asset = rig.WriteMedia("sound.wav", "a sound");
        // 5e-6s overrun: inside the old import bound (1e-5s) but far outside one
        // frame of boundary noise at 24fps (~4.2e-7s) — exactly the gap in which
        // the edit renderer refused work after every frame had been captured.
        var over = new FilmPlan { FrameRate = 24, Audio = { new FilmAudioCue { Id = "cue", MediaId = asset.Id, At = 0, SourceStart = 0, Duration = asset.Duration + 0.000005 } } };
        var ex = Assert.Throws<ArgumentException>(() => MediaLibrary.Validate(rig.Studio, "proj", over));
        Assert.Contains("Audio cue exceeds imported source duration", ex.Message);
        // Exactly at the boundary stays legal on both sides of the pipeline.
        var edge = new FilmPlan { FrameRate = 24, Audio = { new FilmAudioCue { Id = "cue", MediaId = asset.Id, At = 0, SourceStart = 0, Duration = asset.Duration } } };
        MediaLibrary.Validate(rig.Studio, "proj", edge);
    }
}
