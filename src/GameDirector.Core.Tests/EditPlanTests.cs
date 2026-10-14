using GameDirector.Client;
using Xunit;
namespace GameDirector.Core.Tests;
public class EditPlanTests
{
    [Fact] public void CutRequiresSourceBoundsFrameAlignmentAndReason()
    {
        var durations=new Dictionary<string,double>{{"take",10}};
        var plan=new EditPlan{Sources=new(){{"take","picture.mp4"}},Ranges=new(){new EditRange{Source="take",Start=0,End=1,Reason="reveal"}}};
        EditRenderer.Validate(plan,durations);
        plan.Ranges[0].End=11;Assert.Throws<ArgumentException>(()=>EditRenderer.Validate(plan,durations));
        plan.Ranges[0].End=.01;Assert.Throws<ArgumentException>(()=>EditRenderer.Validate(plan,durations));
        plan.Ranges[0].End=1;plan.Ranges[0].Reason="";Assert.Throws<ArgumentException>(()=>EditRenderer.Validate(plan,durations));
        plan.Ranges[0].Reason="reveal";plan.Ranges[0].Source="unknown";Assert.Throws<ArgumentException>(()=>EditRenderer.Validate(plan,durations));
    }
    [Theory] [InlineData(24)] [InlineData(25)] [InlineData(30)] [InlineData(60)]
    public void EditAlignmentAndSourceBoundsUseTheSameFrameTolerance(int fps)
    {
        var plan = new EditPlan { FrameRate = fps, Ranges = new() { new EditRange { Source = "take", Start = 0, End = 1, Reason = "cut" } } };
        var durations = new Dictionary<string, double> { ["take"] = 1 };
        plan.Ranges[0].End = 1 + .000005 / fps; EditRenderer.Validate(plan, durations);
        plan.Ranges[0].End = 1 + .00002 / fps;
        Assert.Throws<ArgumentException>(() => EditRenderer.Validate(plan, durations));
        durations["take"] = 2;
        Assert.Throws<ArgumentException>(() => EditRenderer.Validate(plan, durations));
        plan.Ranges[0].End = 1; durations["take"] = 1 - .00002 / fps;
        Assert.Throws<ArgumentException>(() => EditRenderer.Validate(plan, durations));
    }
    [Fact] public void AlignmentNoiseCannotCreateAnEmptyEditedRange()
    {
        var plan = new EditPlan { FrameRate = 24, Ranges = new() { new EditRange { Source = "take", Start = 1 + .000002 / 24, End = 1 + .000004 / 24, Reason = "cut" } } };
        Assert.Throws<ArgumentException>(() => EditRenderer.Validate(plan, new Dictionary<string, double> { ["take"] = 2 }));
    }
    [Fact] public void ZeroLengthFadesAreOmittedFromTheChain()
    {
        // afade with d=0 falls back to FFmpeg's default length (~0.92 s at 48 kHz),
        // so a zero-length fade must produce no filter at all.
        Assert.Null(AudioFilters.Fade("in",0,0));
        Assert.Null(AudioFilters.Fade("out",2.5,-1));
        Assert.Equal("afade=t=in:st=0:d=0.03",AudioFilters.Fade("in",0,.03));
        Assert.Equal("afade=t=out:st=2.5:d=1.5",AudioFilters.Fade("out",2.5,1.5));
        var silent=AudioFilters.Chain("aresample=48000",AudioFilters.Fade("in",0,0),AudioFilters.Fade("out",1,0));
        Assert.Equal("aresample=48000",silent);
        var faded=AudioFilters.Chain("aresample=48000",AudioFilters.Fade("in",0,.25),AudioFilters.Fade("out",9,1));
        Assert.Equal("aresample=48000,afade=t=in:st=0:d=0.25,afade=t=out:st=9:d=1",faded);
        Assert.DoesNotContain(",,",faded);Assert.DoesNotContain(",,",silent);
    }
    [Fact] public void MissingExplicitMovingShotEndpointsAreRejected()
    {
        var asset=TestAssets.ValidTimeline();asset.Cues[0].Shot.Type="dolly";asset.Cues[0].Shot.To=null;
        Assert.True(GameDirector.Core.Compilation.TimelineCompiler.Compile(asset,TestAssets.Manifest()).HasErrors);
    }
}
