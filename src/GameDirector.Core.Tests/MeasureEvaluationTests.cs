using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class MeasureEvaluationTests
{
    private static TimelineAsset Authored()=>new(){Id="alternates",Cues=new(){
        new(){T=0,Type="camera.shot",Aspect=new(){Min=0,Max=1},Shot=new(){From="portrait-first",Subject="hero"}},
        new(){T=0,Type="camera.shot",Aspect=new(){Min=1},Shot=new(){From="land-first",Subject="hero"}},
        new(){T=1,Type="camera.shot",Aspect=new(){Min=0,Max=1},Shot=new(){From="portrait-second",Subject="hero"}},
        new(){T=1,Type="camera.shot",Aspect=new(){Min=1},Shot=new(){From="land-second",Subject="hero"}}}};
    private static TimelineAsset Interleaved()=>new(){Id="interleaved",Cues=new(){
        new(){T=0,Type="marker",Label="start"},
        new(){T=.15,Type="camera.shot",Aspect=new(){Min=1},Shot=new(){From="cam-start",Subject="foe"}},
        new(){T=.15,Type="camera.shot",Aspect=new(){Min=0,Max=1},Shot=new(){From="cam-start-portrait",Subject="foe"}},
        new(){T=.35,Type="actor.move",Role="foe",To="front"},
        new(){T=.85,Type="actor.anim",Role="foe",Clip="idle"},
        new(){T=1.65,Type="actor.move",Role="foe",To="side"},
        new(){T=2.15,Type="camera.shot",Aspect=new(){Min=1},Shot=new(){From="cam-middle",Subject="foe"}},
        new(){T=2.15,Type="camera.shot",Aspect=new(){Min=0,Max=1},Shot=new(){From="cam-middle-portrait",Subject="foe"}},
        new(){T=3.35,Type="actor.anim",Role="foe",Clip="turn"},
        new(){T=4.1,Type="camera.shot",Aspect=new(){Min=1},Shot=new(){From="cam-b",Subject="foe"}},
        new(){T=4.1,Type="camera.shot",Aspect=new(){Min=0,Max=1},Shot=new(){From="cam-b-portrait",Subject="foe"}},
        new(){T=4.25,Type="actor.anim",Role="foe",Clip="idle"},
        new(){T=4.35,Type="actor.move",Role="foe",To="front"},
        new(){T=5.35,Type="actor.anim",Role="foe",Clip="turn"}}};
    private static MeasureSample Sample(int frame=6)=>new(){Frame=frame,Time=(double)frame/6,Camera=new(){Position=new[]{6d,3,2.5},Right=new[]{Math.Cos(Math.PI/18),Math.Sin(Math.PI/18),0},Up=new[]{-Math.Sin(Math.PI/18),Math.Cos(Math.PI/18),0},Forward=new[]{0d,0,-1},Fov=50,Aspect=16d/9},Composition=new(){Active=true,Subject="hero",Roles=new(){new(){Role="hero",Kind="subject",State="in-front",Rect=new[]{.2,.2,.8,.8},Crown=new[]{.5,.8,3}}}}};
    private static JsonElement Raw(MeasureSample s)=>JsonSerializer.SerializeToElement(s,DslJson.Options);
    // Looking up in the authored array chooses the wrong last alternate/index.
    [Theory] [InlineData(16d/9,"land-second",3)] [InlineData(9d/16,"portrait-second",2)]
    public void ShotComesFromResolvedTimelineAndMapsBackToAuthored(double aspect,string from,int authored)
    {
        var input=Authored();var before=DslJson.Serialize(input);var resolved=AspectHints.Resolve(input,aspect,out var mapping);
        var rows=MeasureRecorder.ReadMeasures(new[]{Raw(Sample())},resolved,mapping,new[]{6},6);var shot=Assert.Single(rows).Shot;
        Assert.Equal(1,shot.ResolvedCue);Assert.Equal(authored,shot.AuthoredCue);Assert.Equal(from,shot.From);Assert.Equal(1,shot.T);
        Assert.Equal(before,DslJson.Serialize(input));Assert.Equal(new[]{aspect>1?1:0,authored},mapping);
        Assert.InRange(Math.Abs(rows[0].HorizonRoll-10),0,1e-12);
    }
    [Theory] [InlineData(16d/9,9,"cam-b")] [InlineData(9d/16,10,"cam-b-portrait")]
    public void AuthoredIndexSurvivesInterleavedCuesAndEarlierAlternateGroups(double aspect,int authored,string from)
    {
        var input=Interleaved();
        Assert.True(input.Cues.Max(c=>c.T)>28d/6, "fixture must extend beyond the measured frame");
        Assert.Equal(from,input.Cues[authored].Shot.From);var resolved=AspectHints.Resolve(input,aspect,out var mapping);
        var sample=Sample(28);sample.Composition.Subject="foe";sample.Composition.Roles[0].Role="foe";
        var rows=MeasureRecorder.ReadMeasures(new[]{Raw(sample)},resolved,mapping,new[]{28},6);
        Assert.Equal(authored,rows[0].Shot.AuthoredCue);Assert.Equal(from,rows[0].Shot.From);Assert.Equal(4.1,rows[0].Shot.T);
    }
    [Fact] public void UnsortedDuplicateListsNormalizeInclusiveRanges()
    {Assert.Equal(new[]{1,2,3,7},FrameSelection.Parse("7,3,1-3,2,7",8));}
    // Exact frame identity includes count, duplicates, missing and unexpected indices.
    [Theory] [InlineData("short")] [InlineData("extra")] [InlineData("duplicate")] [InlineData("late")]
    public void AnyCoverageMismatchIsEngineProtocol(string kind)
    {
        var resolved=AspectHints.Resolve(Authored(),16d/9,out var mapping);var raw=new[]{Raw(Sample(6)),Raw(Sample(7))};
        raw=kind switch{"short"=>raw.Take(1).ToArray(),"extra"=>raw.Append(Raw(Sample(8))).ToArray(),"duplicate"=>new[]{raw[0],raw[0]},_=>new[]{raw[0],Raw(Sample(8))}};
        Assert.Throws<EngineProtocolException>(()=>MeasureRecorder.ReadMeasures(raw,resolved,mapping,new[]{6,7},6));
    }
    [Fact] public void WrongTimeIsEngineProtocolWhileAcceptedTimeIsCanonicalFrameClock()
    {
        var resolved=AspectHints.Resolve(Authored(),16d/9,out var mapping);var s=Sample();s.Time+=FrameClock.AlignmentToleranceFrames/6/2;
        Assert.Equal(1,MeasureRecorder.ReadMeasures(new[]{Raw(s)},resolved,mapping,new[]{6},6)[0].Time);
        s.Time+=.1;Assert.Throws<EngineProtocolException>(()=>MeasureRecorder.ReadMeasures(new[]{Raw(s)},resolved,mapping,new[]{6},6));
    }
    [Theory] [InlineData("null")] [InlineData("1e999")] [InlineData("\"NaN\"")]
    public void NullOrInvalidNumericJsonAreEngineProtocol(string token)
    {
        var resolved=AspectHints.Resolve(Authored(),16d/9,out var mapping);var json=DslJson.Serialize(Sample()).Replace("\"fov\": 50","\"fov\": "+token);
        Assert.Throws<EngineProtocolException>(()=>MeasureRecorder.ReadMeasures(new[]{JsonDocument.Parse(json).RootElement.Clone()},resolved,mapping,new[]{6},6));
    }
    [Fact] public void NonFiniteVectorsAndRolesAreRejectedWithoutRoundingBounds()
    {
        var resolved=AspectHints.Resolve(Authored(),16d/9,out var mapping);var s=Sample();var r=s.Composition.Roles[0];r.Rect=new[]{.123456789012345,.2,.8,.8};
        Assert.Equal(r.Rect,MeasureRecorder.ReadMeasures(new[]{Raw(s)},resolved,mapping,new[]{6},6)[0].Composition.Roles[0].Rect);
        s.Camera.Up=new[]{0d,double.NaN,0};Assert.Throws<ArgumentException>(()=>MeasureEvaluation.Evaluate(new[]{s},resolved,mapping,new[]{6},6));
        s=Sample();s.Composition.Roles[0].Crown=new[]{0d,0,double.PositiveInfinity};Assert.Throws<ArgumentException>(()=>MeasureEvaluation.Evaluate(new[]{s},resolved,mapping,new[]{6},6));
    }
    [Theory] [InlineData(1,90)] [InlineData(-1,-90)] [InlineData(2,90)]
    public void HorizonClampsDotButRejectsNonFiniteInput(double y,double want)
    {Assert.Equal(want,MeasureEvaluation.HorizonRoll(new[]{0d,y,0}));Assert.Throws<ArgumentException>(()=>MeasureEvaluation.HorizonRoll(new[]{0d,double.NaN,0}));}
}
