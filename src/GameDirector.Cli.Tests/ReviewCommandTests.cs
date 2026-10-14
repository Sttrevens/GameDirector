using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Cli.Tests;
public class ReviewCommandTests : TempPaths
{
    private static async Task<(int Code,JsonElement Json)> Run(string root,params string[] extra)
    {
        var stdout=new StringWriter();var stderr=new StringWriter();var code=await Program.Run(new[]{"review",root}.Concat(extra).ToArray(),stdout,stderr);
        Assert.True(stdout.ToString().StartsWith("{"),stderr.ToString());return (code,JsonDocument.Parse(stdout.ToString()).RootElement.Clone());
    }
    private static void Job(string root,string state,CompositionRole role,string? next=null,int frames=1)
    {
        Directory.CreateDirectory(root);DslJson.Save(Path.Combine(root,"timeline.json"),new TimelineAsset{Id="review",Cues=new(){new(){Type="camera.shot",Shot=new(){Subject="hero",DurationSeconds=frames}}}});
        DslJson.Save(Path.Combine(root,"manifest.json"),new CapabilityManifest());
        TakeJobs.Save(root,new(){State=state,TakeId="take",FrameRate=1,Width=64,Height=64,ExpectedFrames=frames,NextAttempt=next,
            TimelineSha256=MediaTools.Hash(Path.Combine(root,"timeline.json")),ManifestSha256=MediaTools.Hash(Path.Combine(root,"manifest.json"))});
        var sample=new CompositionSample{Composition=new(){Active=true,Subject="hero",Roles=new(){role}}};
        DslJson.Save(Path.Combine(root,"receipt.json"),new TakeSnapshot{Take=new(){TakeId="take",State=TakeStates.Completed,FrameCount=frames,CapturedFrames=frames},Reviews=new[]{JsonSerializer.SerializeToElement(sample,DslJson.Options)}});
    }
    private static CompositionRole Good()=>new(){Role="hero",Kind="subject",State="in-front",Rect=new[]{.2,.2,.8,.8},Crown=new[]{.5,.8,4}};
    [Fact] public async Task VerifiedReviewPrintsJsonAndErrorsSetExitOne()
    {
        var root=TempPath("gd-review-");var role=Good();role.State="behind";Job(root,TakeJobStates.Verified,role);
        var (code,json)=await Run(root);Assert.Equal(1,code);Assert.True(json.GetProperty("judged").GetBoolean());Assert.Equal("Verified",json.GetProperty("state").GetString());Assert.Equal(1,json.GetProperty("review").GetProperty("errors").GetInt32());
    }
    [Fact] public async Task FollowsLatestAttemptInsteadOfStaleRootReceipt()
    {
        var root=TempPath("gd-review-");var lost=Good();lost.State="behind";Job(root,TakeJobStates.Interrupted,lost,"attempts/latest");Job(Path.Combine(root,"attempts/latest"),TakeJobStates.Verified,Good());
        var (code,json)=await Run(root);Assert.Equal(0,code);Assert.Equal("Verified",json.GetProperty("state").GetString());Assert.EndsWith(Path.Combine("attempts","latest"),json.GetProperty("directory").GetString());Assert.Equal(0,json.GetProperty("review").GetProperty("errors").GetInt32());
    }
    [Fact] public async Task LatestNonVerifiedIsReportedWithoutJudgingRejectedReceipt()
    {
        var root=TempPath("gd-review-");Job(root,TakeJobStates.Verified,Good(),"attempts/latest");var child=Path.Combine(root,"attempts/latest");Job(child,TakeJobStates.Interrupted,Good());File.WriteAllText(Path.Combine(child,"receipt.json"),"{rejected");
        var (code,json)=await Run(root);Assert.Equal(1,code);Assert.False(json.GetProperty("judged").GetBoolean());Assert.Equal("Interrupted",json.GetProperty("state").GetString());Assert.False(json.TryGetProperty("review",out _));
    }
    [Fact] public async Task UnusedAttemptPointerFallsBackWithoutWritingJob()
    {
        var root=TempPath("gd-review-");Job(root,TakeJobStates.Verified,Good(),"attempts/not-created");var hash=MediaTools.Hash(Path.Combine(root,"job.json"));
        var (code,json)=await Run(root);Assert.Equal(0,code);Assert.Equal(Path.GetFullPath(root),json.GetProperty("directory").GetString());Assert.Equal(hash,MediaTools.Hash(Path.Combine(root,"job.json")));
    }
    [Fact] public async Task TikTokSafeAreaWarningDoesNotFailCommand()
    {
        var root=TempPath("gd-review-");var role=Good();role.Crown=new[]{.5,.18,4};Job(root,TakeJobStates.Verified,role);
        var (code,json)=await Run(root,"--safe-area","tiktok");Assert.Equal(0,code);Assert.True(json.GetProperty("review").GetProperty("warnings").GetInt32()>0);Assert.Equal(0,json.GetProperty("review").GetProperty("errors").GetInt32());
    }
    [Fact] public async Task NumericSafeAreaStillWarnsWithoutFailing()
    {
        var root=TempPath("gd-fmt23-review-");var role=Good();role.Crown=new[]{.5,.05,4};Job(root,TakeJobStates.Verified,role);var (code,json)=await Run(root,"--safe-area",".1,.1,.1,.1");Assert.Equal(0,code);Assert.Equal(1,json.GetProperty("review").GetProperty("warnings").GetInt32());
    }
    [Fact] public async Task UnknownSafeAreaPresetListsKnownNamesAsUsageError()
    {
        var stdout=new StringWriter();var stderr=new StringWriter();Assert.Equal(2,await Program.Run(new[]{"review","unused","--safe-area","unknown-platform"},stdout,stderr));Assert.Empty(stdout.ToString());Assert.Contains("known presets",stderr.ToString());Assert.Contains("tiktok",stderr.ToString());
    }
    [Theory] [InlineData("\"NaN\"")] [InlineData("1e999")] [InlineData("null")]
    public async Task OneMalformedSampleDoesNotPreventGoodSamplesBeingJudged(string bad)
    {
        var root=TempPath("gd-fmt23-review-");Job(root,TakeJobStates.Verified,Good(),frames:3);var receipt=DslJson.Load<TakeSnapshot>(Path.Combine(root,"receipt.json"));
        var lost=Good();lost.State="behind";receipt.Reviews=new[]{receipt.Reviews![0],JsonDocument.Parse("{\"frame\":1,\"time\":1,\"composition\":{\"active\":true,\"subject\":\"hero\",\"roles\":[{\"role\":\"hero\",\"kind\":\"subject\",\"state\":\"in-front\",\"crown\":[0.5,"+bad+",4]}]}}").RootElement.Clone(),JsonSerializer.SerializeToElement(new CompositionSample{Frame=2,Time=2,Composition=new(){Active=true,Subject="hero",Roles=new(){lost}}},DslJson.Options)};DslJson.Save(Path.Combine(root,"receipt.json"),receipt);
        var (code,json)=await Run(root);Assert.Equal(1,code);Assert.True(json.GetProperty("judged").GetBoolean());var v=json.GetProperty("review");Assert.Equal(1,v.GetProperty("errors").GetInt32());Assert.Equal(1,v.GetProperty("warnings").GetInt32());
        Assert.Contains(v.GetProperty("findings").EnumerateArray(),f=>f.GetProperty("code").GetString()=="malformed-sample"&&f.GetProperty("severity").GetString()=="warning"&&f.GetProperty("frame").GetInt32()==1);
        Assert.Contains(v.GetProperty("findings").EnumerateArray(),f=>f.GetProperty("code").GetString()=="lost"&&f.GetProperty("frame").GetInt32()==2);
    }
    [Fact] public async Task MalformedSampleWithoutKnownIndexDoesNotInventFrameZero()
    {
        var root=TempPath("gd-fmt23-review-");Job(root,TakeJobStates.Verified,Good());var receipt=DslJson.Load<TakeSnapshot>(Path.Combine(root,"receipt.json"));receipt.Reviews=new[]{receipt.Reviews![0],JsonDocument.Parse("\"not a sample\"").RootElement.Clone()};DslJson.Save(Path.Combine(root,"receipt.json"),receipt);
        var (code,json)=await Run(root);Assert.Equal(0,code);Assert.True(json.GetProperty("judged").GetBoolean());var f=Assert.Single(json.GetProperty("review").GetProperty("findings").EnumerateArray());Assert.Equal("malformed-sample",f.GetProperty("code").GetString());Assert.False(f.TryGetProperty("frame",out _));
    }
    [Fact] public async Task RejectedVerifiedReceiptStillPrintsJobStateWithoutJudging()
    {
        var root=TempPath("gd-review-");Job(root,TakeJobStates.Verified,Good());var receipt=DslJson.Load<TakeSnapshot>(Path.Combine(root,"receipt.json"));receipt.Take!.TakeId="stale";DslJson.Save(Path.Combine(root,"receipt.json"),receipt);
        var (code,json)=await Run(root);Assert.Equal(1,code);Assert.False(json.GetProperty("judged").GetBoolean());Assert.Equal("Verified",json.GetProperty("state").GetString());Assert.False(json.TryGetProperty("review",out _));Assert.Contains("receipt",json.GetProperty("note").GetString());
    }
    [Theory] [InlineData(".1,.1,.1")] [InlineData("NaN,0,0,0")] [InlineData(".6,.6,0,0")] [InlineData("-1,0,0,0")] public async Task MalformedSafeAreaIsUsageError(string area)
    {
        var stdout=new StringWriter();var stderr=new StringWriter();Assert.Equal(2,await Program.Run(new[]{"review","unused","--safe-area",area},stdout,stderr));Assert.Empty(stdout.ToString());Assert.Contains("--safe-area",stderr.ToString());
    }
}
