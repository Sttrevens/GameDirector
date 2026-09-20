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
    [Fact] public void MissingExplicitMovingShotEndpointsAreRejected()
    {
        var asset=TestAssets.ValidTimeline();asset.Cues[0].Shot.Type="dolly";asset.Cues[0].Shot.To=null;
        Assert.True(GameDirector.Core.Compilation.TimelineCompiler.Compile(asset,TestAssets.Manifest()).HasErrors);
    }
}
