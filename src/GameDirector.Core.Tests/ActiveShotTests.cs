using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class ActiveShotTests
{
    private static Cue Shot(double time)=>new(){T=time,Type="camera.shot",Shot=new(){Subject="hero",From="front"}};
    [Fact] public void EqualTimeTieSelectsLastStoredCue()
    {var a=new TimelineAsset{Cues=new(){Shot(1),Shot(1)}};Assert.Equal(1,ActiveShot.CueIndex(a,1));}
    [Fact] public void UnsortedCuesSelectLatestTimeNotLatestArrayPosition()
    {var a=new TimelineAsset{Cues=new(){Shot(2),Shot(0),Shot(1)}};Assert.Equal(0,ActiveShot.CueIndex(a,2));Assert.Equal(2,ActiveShot.CueIndex(a,1));}
    [Fact] public void ExactCueEpsilonBoundaryIsInclusiveButBeyondItIsNot()
    {
        var a=new TimelineAsset{Cues=new(){Shot(1+FrameClock.CueEpsilonSeconds)}};
        Assert.Equal(0,ActiveShot.CueIndex(a,1));Assert.Null(ActiveShot.CueIndex(a,1-FrameClock.CueEpsilonSeconds));
    }
    [Fact] public void NonShotAndNullCuesCannotReplaceCameraProvenance()
    {var a=new TimelineAsset{Cues=new(){Shot(0),new(){T=1,Type="marker"},null!}};Assert.Equal(0,ActiveShot.CueIndex(a,2));Assert.Null(ActiveShot.CueIndex(new TimelineAsset(),2));}
    [Fact] public void NullTimelineOrCueListHasNoActiveShot()
    {Assert.Null(ActiveShot.CueIndex(null!,0));Assert.Null(ActiveShot.CueIndex(new(){Cues=null!},0));}
    // The identity map for ordinary multi-cue assets cannot silently become i+1.
    [Fact] public void UnhintedMultiCueMeasuresPreserveStoredAndAuthoredIndexIdentity()
    {
        var a=new TimelineAsset{Cues=new(){new(){T=0,Type="marker"},Shot(0),new(){T=1,Type="marker"},Shot(1)}};
        var resolved=AspectHints.Resolve(a,16d/9,out var mapping);var frames=new[]{0,5,6,7};
        var samples=frames.Select(f=>new MeasureSample{Frame=f,Time=(double)f/6,Camera=new(){Position=new[]{0d,1,3},Right=new[]{1d,0,0},Up=new[]{0d,1,0},Forward=new[]{0d,0,-1},Fov=50,Aspect=1},Composition=new(){Active=true,Subject="hero",Roles=new()}}).ToArray();
        var raw=samples.Select(s=>JsonSerializer.SerializeToElement(s,DslJson.Options)).ToArray();
        var rows=MeasureRecorder.ReadMeasures(raw,resolved,mapping,frames,6);
        Assert.Equal(new[]{1,1,3,3},rows.Select(r=>r.Shot.ResolvedCue));Assert.All(rows,r=>Assert.Equal(r.Shot.ResolvedCue,r.Shot.AuthoredCue));
    }
    [Theory] [InlineData(.5,6,3)] [InlineData(.50000001,6,4)] [InlineData(1.0000001,30,31)] [InlineData(0,6,0)]
    public void FirstShowingCueUsesDispatchTimeNotDurationAlignment(double time,int fps,int want)
    {
        Assert.Equal(want,FrameClock.FirstShowingCue(time,fps));Assert.True(FrameClock.TimeOf(want,fps)+FrameClock.CueEpsilonSeconds>=time);
        if(want>0)Assert.True(FrameClock.TimeOf(want-1,fps)+FrameClock.CueEpsilonSeconds<time);
    }
}
