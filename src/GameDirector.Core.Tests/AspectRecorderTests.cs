using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
[Collection("DistributionEnv")]
public class AspectRecorderTests : TempPaths
{
    [Fact] public void ResolveIsIdentityWithoutHintsAndCopiesHintedPayloads()
    {
        var plain=TestAssets.ValidTimeline();Assert.Same(plain,AspectHints.Resolve(plain));
        var l=TestAssets.Shot(0,duration:1);l.Aspect=new(){Min=1};l.Shot.With=new(){"sentinel"};var p=TestAssets.Shot(0,from:"loc_stage",duration:1);p.Aspect=new(){Max=1};
        var asset=new TimelineAsset{Id="hints",Cues=new(){l,p},Metadata=new(){{"source","authored"}}};var resolved=AspectHints.Resolve(asset,16d/9);
        Assert.NotSame(asset,resolved);Assert.Null(Assert.Single(resolved.Cues).Aspect);resolved.Cues[0].Shot.With!.Clear();resolved.Metadata["source"]="changed";
        Assert.Single(l.Shot.With);Assert.Equal("authored",asset.Metadata["source"]);Assert.NotNull(l.Aspect);Assert.Equal("loc_stage",Assert.Single(AspectHints.Resolve(asset,9d/16).Cues).Shot.From);
    }
    // The real Record path writes the admitted/resolved inputs before the fixture refuses start.
    [MediaTheory] [InlineData(true,960,540,"cam_north")] [InlineData(true,540,960,"loc_stage")] [InlineData(false,960,540,"cam_north")]
    [InlineData(true,960,540,"cam_north",true)]
    public async Task RecordSendsAndStoresResolvedTimelineWithAuthoredProvenanceOnlyForHints(bool hints,int width,int height,string from,bool changeDuringManifest=false)
    {
        var root=TempPath("gd-aspect-recorder-");Directory.CreateDirectory(Path.Combine(root,"source"));var path=Path.Combine(root,"authored.json");var output=Path.Combine(root,"take");
        var m=TestAssets.Manifest();m.Capabilities[CapabilityKeys.ProjectSourceRoot]=Path.Combine(root,"source");
        var l=TestAssets.Shot(0,duration:1);var p=TestAssets.Shot(0,from:"loc_stage",duration:1);if(hints) { l.Aspect=new(){Min=1};p.Aspect=new(){Max=1}; }
        var asset=new TimelineAsset{Id="formats",Cues=hints?new(){l,p}:new(){l}};DslJson.Save(path,asset);var authoredHash=MediaTools.Hash(path);string? sent=null;
        await using var bridge=new BridgeHttpFixture(async ctx=>{
            if(ctx.Request.Url!.AbsolutePath=="/manifest") { if(changeDuringManifest) { asset.Id="concurrently-edited";DslJson.Save(path,asset); } await BridgeHttpFixture.Reply(ctx,200,DslJson.Serialize(m));return; }
            using var reader=new StreamReader(ctx.Request.InputStream);sent=await reader.ReadToEndAsync();await BridgeHttpFixture.Reply(ctx,409,"{\"error\":\"fixture stop\"}");
        });
        await Assert.ThrowsAsync<BridgeException>(()=>TakeRecorder.Record(path,output,bridge.Endpoint,6,width,height));
        var stored=File.ReadAllText(Path.Combine(output,"timeline.json"));Assert.Equal("formats",DslJson.Deserialize<TimelineAsset>(stored).Id);Assert.DoesNotContain("\"aspect\"",stored);Assert.DoesNotContain("\"aspect\"",sent);
        Assert.Equal(from,Assert.Single(DslJson.Deserialize<TimelineAsset>(stored).Cues).Shot.From);Assert.Equal(stored,DslJson.Serialize(DslJson.Deserialize<TimelineAsset>(JsonNode.Parse(sent!)!["timeline"]!.ToJsonString())));
        var job=JsonNode.Parse(File.ReadAllText(Path.Combine(output,"job.json")))!;Assert.Equal(MediaTools.Hash(Path.Combine(output,"timeline.json")),job["timelineSha256"]!.GetValue<string>());
        if(hints) { Assert.Equal(authoredHash,job["authoredTimelineSha256"]!.GetValue<string>());Assert.NotEqual(authoredHash,job["timelineSha256"]!.GetValue<string>()); }
        else { Assert.False(job.AsObject().ContainsKey("authoredTimelineSha256"));Assert.Equal(File.ReadAllText(path),stored); }
        if(changeDuringManifest)Assert.NotEqual(authoredHash,MediaTools.Hash(path));else Assert.Equal(authoredHash,MediaTools.Hash(path));
    }
}
