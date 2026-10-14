using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class AspectHintTests
{
    private const string Pair="""
        {"version":"0.1","id":"formats","cues":[
          {"t":0,"type":"camera.shot","aspect":{"max":1},"shot":{"subject":"hero","from":"loc_stage","durationSeconds":4}},
          {"t":0,"type":"camera.shot","aspect":{"min":1},"shot":{"subject":"hero","from":"cam_north","durationSeconds":4}},
          {"t":0,"type":"actor.anim","role":"hero","clip":"Idle"},
          {"t":4,"type":"marker","label":"end"}]}
        """;
    private static TimelineAsset Asset(Action<JsonNode>? edit=null) { var json=JsonNode.Parse(Pair)!;edit?.Invoke(json);return DslJson.Deserialize<TimelineAsset>(json.ToJsonString()); }
    private static CompileResult Compile(TimelineAsset asset,int width=960,int height=540)=>TakeAdmission.Admit(asset,TestAssets.Manifest(),6,width,height).Compiled;
    private static void Error(CompileResult result,string code,string path) { Assert.Null(result.Timeline);Assert.Contains(result.Diagnostics,d=>d.Code==code&&d.Path==path&&d.Severity==DiagnosticSeverity.Error); }

    // Keeping both alternates, or making max inclusive, must break exactly-one at 1:1.
    [Fact] public void SquareSelectsExactlyOneMinOneCameraShot()
    {
        var asset=Asset();var result=Compile(asset,1080,1080);Assert.False(result.HasErrors);
        var shot=Assert.Single(result.Timeline.OrderedCues.Where(c=>c.Type==CueTypes.CameraShot&&c.T==0));Assert.Equal("cam_north",shot.Shot.From);
        Assert.DoesNotContain("\"aspect\"",DslJson.Serialize(result.Timeline));Assert.Contains("\"aspect\"",DslJson.Serialize(asset));
    }
    [Fact] public void LandscapePortraitKeepDurationFramesOpeningOverlapAndThreeEventOrder()
    {
        var asset=Asset();var m=TestAssets.Manifest();var l=TakeAdmission.Admit(asset,m,6,960,540);var p=TakeAdmission.Admit(asset,m,6,540,960);
        Assert.True(l.Ok);Assert.True(p.Ok);Assert.Equal(24,l.FrameCount);Assert.Equal(l.FrameCount,p.FrameCount);Assert.Equal(4,l.Compiled.Timeline.Duration);Assert.Equal(l.Compiled.Timeline.Duration,p.Compiled.Timeline.Duration);
        Assert.Equal("cam_north",Assert.Single(l.Compiled.Timeline.OrderedCues.Where(c=>c.Type==CueTypes.CameraShot)).Shot.From);
        Assert.Equal("loc_stage",Assert.Single(p.Compiled.Timeline.OrderedCues.Where(c=>c.Type==CueTypes.CameraShot)).Shot.From);
        Assert.Equal(l.Compiled.Timeline.OrderedCues.Select(c=>(c.T,c.Type,c.Role,c.Label)),p.Compiled.Timeline.OrderedCues.Select(c=>(c.T,c.Type,c.Role,c.Label)));
        Assert.Empty(l.Compiled.Diagnostics);Assert.Empty(p.Compiled.Diagnostics);
        asset.Cues.Add(TestAssets.Shot(3,duration:1));l=TakeAdmission.Admit(asset,m,6,960,540);p=TakeAdmission.Admit(asset,m,6,540,960);
        Assert.Single(l.Compiled.Diagnostics.Where(d=>d.Code==TimelineCompiler.WShotOverlap));Assert.Equal(l.Compiled.Diagnostics.Select(d=>d.ToString()),p.Compiled.Diagnostics.Select(d=>d.ToString()));
    }
    [Fact] public void UnequalDurationsAreRefused()=>Error(Compile(Asset(j=>j["cues"]![1]!["shot"]!["durationSeconds"]=3)),"GD1013","cues[1].shot.durationSeconds");
    [Fact] public void NonCameraAspectIsRefused()=>Error(Compile(Asset(j=>j["cues"]![2]!["aspect"]=new JsonObject())),"GD1013","cues[2].aspect");
    [Fact] public void NoSizeIsGD1013()=>Error(TimelineCompiler.Compile(Asset(),TestAssets.Manifest()),"GD1013","cues[0].aspect");
    [Fact] public void BadPortraitAnchorFailsLandscapeToo()=>Error(Compile(Asset(j=>j["cues"]![0]!["shot"]!["from"]="missing")),"GD1003","cues[0].shot.from");
    [Fact] public void AbsentPortraitSubjectFailsLandscapeToo()=>Error(Compile(Asset(j=>j["cues"]![0]!["shot"]!["subject"]="sentinel")),"GD1011","cues[0].shot.subject");
    [Theory] [InlineData("gap")] [InlineData("overlap")] [InlineData("negative")] [InlineData("empty")] [InlineData("unhinted")]
    public void GroupsMustPartitionPositiveAspects(string kind)
    {
        var result=Compile(Asset(j=>{var cues=j["cues"]!;if(kind=="gap")cues[1]!["aspect"]!["min"]=1.1;if(kind=="overlap")cues[1]!["aspect"]!["min"]=.9;
            if(kind=="negative")cues[1]!["aspect"]!["min"]=-1;if(kind=="empty")cues[1]!["aspect"]!["max"]=1;
            if(kind=="unhinted")cues[1]!.AsObject().Remove("aspect");}));
        Assert.Null(result.Timeline);Assert.Contains(result.Diagnostics,d=>d.Code=="GD1013");
    }
    [Theory] [InlineData("role")] [InlineData("label")]
    public void AlternateEventIdentityCannotVary(string field)
    {
        var result=Compile(Asset(j=>{j["cues"]![0]![field]="hero";j["cues"]![1]![field]="sentinel";}));
        Error(result,"GD1013","cues[1]."+field);
    }
    [Fact] public void InterleavedAlternateOrderIsRefused()
    {
        var result=Compile(Asset(j=>{var cues=j["cues"]!.AsArray();var anim=cues[2]!.DeepClone();cues.RemoveAt(2);cues.Insert(1,anim);}));
        Assert.Null(result.Timeline);Assert.Contains(result.Diagnostics,d=>d.Code=="GD1013"&&d.Message.Contains("contiguous"));
    }
    [Theory] [InlineData("from")] [InlineData("to")] [InlineData("omitted-from")]
    public void CurrentAfterHintedGroupIsRefused(string field)
    {
        var asset=Asset();var shot=field=="omitted-from"?new Cue{T=4,Type=CueTypes.CameraShot,Shot=new(){Subject="hero",DurationSeconds=1}}:TestAssets.Shot(4,duration:1);if(field=="to")shot.Shot.To="current";else if(field=="from")shot.Shot.From="current";asset.Cues.Add(shot);
        Error(Compile(asset),"GD1013","cues[4].shot."+(field=="to"?"to":"from"));
    }
    [Fact] public void ExplicitAnchorResetsCurrentDependency()
    {
        var asset=Asset();asset.Cues.Add(TestAssets.Shot(4,duration:1));asset.Cues.Add(TestAssets.Shot(5,from:"current",duration:1));Assert.False(Compile(asset).HasErrors);
    }
    [Theory] [InlineData(0d,false)] [InlineData(.5d,false)] [InlineData(1d,false)] [InlineData(2d,true)]
    public void ResetMustStrictlyPrecedeCurrentBeyondCueEpsilon(double epsilonSteps,bool valid)
    {
        var asset=Asset();asset.Cues.Add(TestAssets.Shot(6,duration:0));
        var reading=TestAssets.Shot(6+epsilonSteps*FrameClock.CueEpsilonSeconds,type:"dolly",from:"current",duration:1);reading.Shot.To="loc_stage";asset.Cues.Add(reading);
        var result=Compile(asset);if(valid)Assert.False(result.HasErrors);else Error(result,"GD1013","cues[5].shot.from");
    }
    [Theory] [InlineData(6d)] [InlineData(7d)]
    public void DespawnAtResetInstantKeepsCurrentDependency(double readingTime)
    {
        var asset=Asset();asset.Cues.Add(new(){T=5,Type=CueTypes.ActorSpawn,Role="sentinel",Location="loc_gate"});
        asset.Cues.Add(TestAssets.Shot(6,subject:"sentinel",duration:0));asset.Cues.Add(new(){T=6,Type=CueTypes.ActorDespawn,Role="sentinel"});
        asset.Cues.Add(TestAssets.Shot(readingTime,type:"dolly",from:"current",duration:1));asset.Cues[^1].Shot.To="loc_stage";
        Error(Compile(asset),"GD1013","cues[7].shot.from");
    }
    [Theory] [InlineData(0,true)] [InlineData(2,true)] [InlineData(4,false)] [InlineData(5,false)] [InlineData(-1,false)]
    [InlineData(3,false,4,2d)] [InlineData(1,true,4,2d)] [InlineData(10,true,0)] [InlineData(0,true,4,null,true)]
    public void WithMayEnterInsideWindowButMustBecomePresent(double spawn,bool valid,double duration=4,double? cut=null,bool authoredAfter=false)
    {
        var asset=Asset(j=>{j["cues"]![1]!["shot"]!["with"]=new JsonArray("sentinel");j["cues"]![0]!["shot"]!["durationSeconds"]=duration;j["cues"]![1]!["shot"]!["durationSeconds"]=duration;});
        if(cut.HasValue)asset.Cues.Add(TestAssets.Shot(cut.Value,duration:1));
        if(spawn>=0)asset.Cues.Insert(spawn==0&&!authoredAfter?0:3,new Cue{T=spawn,Type=CueTypes.ActorSpawn,Role="sentinel",Location="loc_gate"});
        var result=Compile(asset);if(valid)Assert.False(result.HasErrors);else Assert.Contains(result.Diagnostics,d=>d.Code=="GD1011"&&d.Path.EndsWith("shot.with[0]"));
    }
    [Theory] [InlineData("unknown","GD1001")] [InlineData("duplicate","GD1009")] [InlineData("subject","GD1009")]
    public void WithRejectsUnknownDuplicateAndSubject(string kind,string code)
    {
        var asset=Asset(j=>j["cues"]![1]!["shot"]!["with"]=kind=="duplicate"?new JsonArray("sentinel","sentinel"):new JsonArray(kind=="subject"?"hero":"ghost"));
        asset.Cues.Insert(0,new Cue{T=0,Type=CueTypes.ActorSpawn,Role="sentinel",Location="loc_gate"});var result=Compile(asset);Assert.Contains(result.Diagnostics,d=>d.Code==code&&d.Path.Contains("shot.with"));
    }
    [Fact] public void SnapshotDeepCopiesWithAndAspect()
    {
        var asset=Asset(j=>j["cues"]![1]!["shot"]!["with"]=new JsonArray("sentinel"));var copy=asset.Cues[1].Snapshot();
        copy.Shot.With.Clear();Assert.Equal(new[]{"sentinel"},asset.Cues[1].Shot.With);
        copy.Aspect.Min=2;Assert.Equal(1d,asset.Cues[1].Aspect.Min);
    }
}
