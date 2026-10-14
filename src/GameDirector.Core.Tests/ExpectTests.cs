using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class ExpectTests
{
    private static MeasureRow Row()=>new(){Frame=1,Camera=new(){Position=new[]{1d,2,3},Fov=58},HorizonRoll=-4,Shot=new(){AuthoredCue=9,From="cam-b"},Composition=new(){Active=true,Subject="hero",Roles=new(){new(){Role="hero",Kind="subject",State="in-front",Rect=new[]{.1,.2,.7,.6},Crown=new[]{.4,.75,3}},new(){Role="foe",Kind="with",State="behind"}}}};
    // Swapping margin/headroom, dropping a role state or using width for native height must fail.
    [Fact] public void MetricsUseOneMeasuredRowAndBehindRolesHaveStateOnly()
    {
        var m=RegressionMetrics.Project(Row(),960);var r=m.Roles["hero"];
        Assert.Equal(.4,r["figure.h"].Value!.Value,12);Assert.Equal(384,r["figure.h"].Pixels);
        Assert.Equal(.4,r["figure.cx"].Value!.Value,12);Assert.Equal(.4,r["figure.cy"].Value!.Value,12);
        Assert.Equal(.1,r["margin"].Value!.Value,12);Assert.Equal(.25,r["headroom"].Value!.Value,12);
        Assert.Equal("in-front",r["state"].Text);Assert.Equal("behind",Assert.Single(m.Roles["foe"]).Value.Text);
        Assert.Equal(new[]{1d,2,3},m.Values["camera.position"].Vector);Assert.Equal(58,m.Values["camera.fov"].Value);Assert.Equal(-4,m.Values["horizonRoll"].Value);
    }
    private static TimelineAsset Timeline(params (double T,string From)[] shots)=>new(){Id="plan",Cues=shots.Select(s=>new Cue{T=s.T,Type="camera.shot",Shot=new(){Subject="hero",From=s.From}}).ToList()};
    // A duration-ceiling shortcut or mid+1 mutation loses exact cue-dispatch/frame-interval truth.
    [Fact] public void AutoFramesUseOffGridFirstMidLastAndLastShotRunsToEnd()
    {
        var plan=ExpectFrames.Select(Timeline((.01,"a"),(1.01,"b")),2,6,new[]{new ExpectBeat{Label="beat",Frame=4}});
        Assert.Equal(new[]{1,3,4,6,7,9,11},plan.Select(p=>p.Frame));
        Assert.Equal(new[]{"first"},plan[0].Auto);Assert.Equal(new[]{"mid"},plan[1].Auto);Assert.Equal(new[]{"beat"},plan[2].Beat);Assert.Equal(new[]{"last"},plan[^1].Auto);
        Assert.Throws<ArgumentException>(()=>ExpectFrames.Select(Timeline((.01,"a")),2,6,new[]{new ExpectBeat{Label="bad",Frame=0}}));
    }
    [Fact] public void ResolvedSameTimeAlternatesAndCoincidentAutoLabelsAreNotLost()
    {
        var t=Timeline((0,"portrait"),(0,"landscape"),(.2,"next"));t.Cues[0].Aspect=new(){Max=1};t.Cues[1].Aspect=new(){Min=1};
        foreach(var aspect in new[]{.5,2d}){
            var resolved=AspectHints.Resolve(t,aspect);var p=ExpectFrames.Select(resolved,.5,6,Array.Empty<ExpectBeat>());
            Assert.Equal(aspect<1?"portrait":"landscape",resolved.Cues[ActiveShot.CueIndex(resolved,0)!.Value].Shot.From);
            Assert.Equal(new[]{0,1,2},p.Select(f=>f.Frame));Assert.Equal(new[]{"first","mid"},p[0].Auto);Assert.Equal(new[]{"last"},p[1].Auto);Assert.Equal(new[]{"first","mid","last"},p[2].Auto);
        }
    }
}
