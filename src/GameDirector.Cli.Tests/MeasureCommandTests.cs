using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Cli.Tests;
public class MeasureCommandTests : TempPaths
{
    private sealed record Result(int Code,string Out,string Error,string Dir,string Input,int Starts,int Manifests,List<FrameRequest> Steps,List<string> Stops,List<JsonElement> Rows);
    private async Task<Result> Measure(string spec,bool stamp=true,double first=0,string skew="",bool hinted=false,string fault="",bool existing=false,string stampValue="measure-v1",double duration=1,bool retention=false)
    {
        var root=TempPath("gd-measure-cli-");Directory.CreateDirectory(root);var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        var timeline=Path.Combine(root,"authored.json");var output=Path.Combine(root,"measure");if(existing)Directory.CreateDirectory(output);
        DslJson.Save(timeline,new TimelineAsset{Id="measure-cli",Cues=new(){new(){T=first,Type="camera.shot",Shot=new(){Subject="hero",From="front",Frame="full",DurationSeconds=duration}}}});
        if(hinted){var asset=DslJson.Load<TimelineAsset>(timeline);var portrait=asset.Cues[0];portrait.Aspect=new(){Max=1};var landscape=portrait.Snapshot();landscape.Aspect=new(){Min=1};asset.Cues.Add(landscape);DslJson.Save(timeline,asset);}
        var manifest=new CapabilityManifest{Roles=new(){new(){Id="hero",DefaultActor="body",PresentAtStart=true}},Actors=new(){new(){Id="body"}},Locations=new(){new(){Id="front",Position=new[]{0f,1,3}}},
            ShotTypes=new(){"lockoff"},FrameTypes=new(){"full"},Capabilities=new(){[CapabilityKeys.DirectorMode]=DirectorModes.OfflineSandbox,[CapabilityKeys.ProjectSourceRoot]=source}};
        if(stamp)manifest.Capabilities["measure"]=stampValue;
        var server=LoopbackHttp.Start();var steps=new List<FrameRequest>();var rows=new List<JsonElement>();var stops=new List<string>();var starts=0;var manifests=0;
        var count=first+duration<=600?FrameClock.FrameCount(first+duration,6):6;var capture=0;using var ct=new CancellationTokenSource();
        TakeReceipt Receipt(string state)=>new(){TakeId="take",TimelineId="measure-cli",State=state,FrameRate=6,Width=64,Height=64,FrameCount=count,CaptureFromFrame=capture,CapturedFrames=steps.Count(s=>!s.AdvanceOnly)};
        var loop=Task.Run(async()=>{
            try{while(true){var ctx=await server.Listener.GetContextAsync();byte[] bytes;ctx.Response.ContentType="application/json";var cancel=false;
                switch(ctx.Request.Url!.AbsolutePath){
                    case "/manifest":manifests++;bytes=Encoding.UTF8.GetBytes(DslJson.Serialize(manifest));break;
                    case "/take/start":starts++;using(var r=new StreamReader(ctx.Request.InputStream)){var request=DslJson.Deserialize<TakeRequest>(await r.ReadToEndAsync());capture=request.CaptureFromFrame;}
                        var start=Receipt(TakeStates.Capturing);if(fault=="start")start.FrameCount++;bytes=Encoding.UTF8.GetBytes(DslJson.Serialize(start));break;
                    case "/take/frame":FrameRequest step;using(var r=new StreamReader(ctx.Request.InputStream))step=DslJson.Deserialize<FrameRequest>(await r.ReadToEndAsync());steps.Add(step);
                        if(fault=="frame"&&step.FrameIndex==2){ctx.Response.StatusCode=400;bytes=Encoding.UTF8.GetBytes("{\"code\":\"invalid_request\",\"error\":\"injected frame failure\"}");break;}
                        var comp=new CompositionReport{Active=true,Subject="hero",Center=retention?null:new[]{.5,.5,3},Roles=new(){new(){Role="hero",Kind="subject",State=retention?"behind":"in-front",Rect=retention?null:new[]{.2,.2,.8,.8},Crown=retention?null:new[]{.5,.8,3}}},
                            ShotType=retention?"lockoff":null,VisibleSamples=retention?17:null,TotalSamples=retention?23:null,Note=retention?"engine note":null};
                        if(step.Measure)rows.Add(JsonSerializer.SerializeToElement(new{frame=step.FrameIndex,time=(double)step.FrameIndex/6+(skew=="time"?.1:0),camera=new{position=new[]{0d,1,3},right=new[]{1d,0,0},up=new[]{0d,1,0},forward=new[]{0d,0,-1},fov=50d,aspect=1d},composition=comp},DslJson.Options));
                        if(step.AdvanceOnly){ctx.Response.StatusCode=204;bytes=Array.Empty<byte>();}else{ctx.Response.ContentType="image/png";bytes=new byte[]{137,80,78,71,13,10,26,10,(byte)step.FrameIndex};}
                        cancel=fault=="cancel"&&step.FrameIndex==2;break;
                    case "/take/stop":using(var r=new StreamReader(ctx.Request.InputStream)){var body=JsonDocument.Parse(await r.ReadToEndAsync());stops.Add(body.RootElement.GetProperty("takeId").GetString()!);}bytes=Encoding.UTF8.GetBytes("{}");break;
                    case "/take":bytes=Encoding.UTF8.GetBytes(DslJson.Serialize(new{take=Receipt(TakeStates.Completed),events=Array.Empty<object>(),reviews=Array.Empty<object>(),measures=skew=="short"?rows.Skip(1):rows}));break;
                    default:bytes=Encoding.UTF8.GetBytes("{}");break;
                }
                ctx.Response.ContentLength64=bytes.Length;await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();if(cancel)ct.Cancel();
            }}catch(Exception e)when(e is HttpListenerException or ObjectDisposedException or ArgumentException){}
        });
        var stdout=new StringWriter();var stderr=new StringWriter();int code;
        try{code=await Program.Run(new[]{"measure",timeline,"--frames",spec,"--out",output,"--endpoint",server.Endpoint,"--fps","6","--width","64","--height","64"},stdout,stderr,ct.Token);}
        finally{server.Dispose();await loop;}
        return new(code,stdout.ToString(),stderr.ToString(),output,timeline,starts,manifests,steps,stops,rows);
    }
    [Fact] public async Task MissingCapabilityPrintsCannotMeasureAndStartsNothing()
    {
        var r=await Measure("1",stamp:false);Assert.Equal(1,r.Code);var json=JsonDocument.Parse(r.Out).RootElement;
        Assert.False(json.GetProperty("measured").GetBoolean());Assert.StartsWith("cannot measure here:",json.GetProperty("note").GetString());Assert.Equal(0,r.Starts);Assert.Empty(r.Steps);Assert.False(Directory.Exists(r.Dir));
    }
    [Fact] public async Task SparseFramesNormalizeAndSaveOnlyMeasuredPngsWithoutEncoding()
    {
        var r=await Measure("3,1-2,2");Assert.Equal(0,r.Code);var json=JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal(new[]{1,2,3},json.GetProperty("frames").EnumerateArray().Select(n=>n.GetInt32()));Assert.Equal(3,json.GetProperty("rows").GetArrayLength());
        Assert.Equal(Enumerable.Range(0,6),r.Steps.Select(s=>s.FrameIndex));Assert.Equal(new[]{1,2,3},r.Steps.Where(s=>s.Measure).Select(s=>s.FrameIndex));Assert.True(r.Steps[0].AdvanceOnly);
        Assert.Equal(new[]{"000001.png","000002.png","000003.png"},Directory.GetFiles(Path.Combine(r.Dir,"frames")).Select(Path.GetFileName).Order());
        foreach(var i in new[]{1,2,3})Assert.Equal((byte)i,File.ReadAllBytes(Path.Combine(r.Dir,"frames",$"{i:D06}.png"))[8]);
        Assert.False(File.Exists(Path.Combine(r.Dir,"picture.mp4")));Assert.True(JsonNode.DeepEquals(JsonNode.Parse(r.Out),JsonNode.Parse(File.ReadAllText(Path.Combine(r.Dir,"measure.json")))));
    }
    [Fact] public async Task RecorderUsesStoredResolvedTimelineAndPreservesAuthoredIndex()
    {
        var r=await Measure("1",hinted:true);Assert.Equal(0,r.Code);var shot=JsonDocument.Parse(r.Out).RootElement.GetProperty("rows")[0].GetProperty("shot");
        Assert.Equal(0,shot.GetProperty("resolvedCue").GetInt32());Assert.Equal(1,shot.GetProperty("authoredCue").GetInt32());
    }
    [Theory] [InlineData("6")] [InlineData("-1")] [InlineData("2-1")] [InlineData("1,,2")] [InlineData("x")] [InlineData("2147483648")]
    public async Task BadOrOutOfRangeFramesAreUsageErrorsAndStartNothing(string spec)
    {var r=await Measure(spec);Assert.Equal(2,r.Code);Assert.Empty(r.Out);Assert.Contains("--frames",r.Error);Assert.Equal(0,r.Starts);}
    [Fact] public async Task BeforeFirstShotRefusalNamesFirstShotFrame()
    {var r=await Measure("1",first:.5);Assert.Equal(1,r.Code);Assert.Contains("first camera shot frame 3",r.Error);Assert.Equal(0,r.Starts);}
    [Theory] [InlineData("x","item 'x' is not a number")] [InlineData("2147483648","item '2147483648' is out of int range")] [InlineData("6","item '6' contains index 6; last frame index is 5")]
    public async Task FrameErrorNamesItemAndDistinguishesSyntaxIntegerAndTimelineRange(string spec,string message)
    {var r=await Measure(spec);Assert.Equal(2,r.Code);Assert.StartsWith("--frames: "+message,r.Error);Assert.Equal(0,r.Starts);if(spec!="6")Assert.Equal(0,r.Manifests);}
    [Theory] [InlineData("x")] [InlineData("2147483648")] [InlineData("1,,2")] [InlineData("2-1")]
    public async Task SyntaxIsRejectedBeforeMissingFileOrUnreachableBridge(string spec)
    {
        var stdout=new StringWriter();var stderr=new StringWriter();
        var code=await Program.Run(new[]{"measure",Path.Combine(TempPath("gd-measure-missing-"),"missing.json"),"--frames",spec,"--out",TempPath("gd-measure-out-"),"--endpoint","http://127.0.0.1:1"},stdout,stderr);
        Assert.Equal(2,code);Assert.Empty(stdout.ToString());Assert.StartsWith("--frames:",stderr.ToString());
    }
    [Theory] [InlineData(0,0)] [InlineData(1e10,1)]
    public async Task DurationAdmissionPrecedesFrameRangeAndCannotOverflow(double first,double duration)
    {var r=await Measure("0",first:first,duration:duration);Assert.Equal(1,r.Code);Assert.Contains("take duration must be >0 and <=600 seconds",r.Error);Assert.Equal(0,r.Starts);Assert.False(Directory.Exists(r.Dir));}
    [Fact] public async Task JustAfterFrameBoundaryNamesFirstFrameThatActuallyShowsShot()
    {var r=await Measure("3",first:.50000001);Assert.Equal(1,r.Code);Assert.Contains("first camera shot frame 4",r.Error);Assert.Equal(0,r.Starts);}
    // Every failure after a returned receipt must release the actual take ID, with an uncancelled cleanup token.
    [Theory] [InlineData("start")] [InlineData("cancel")] [InlineData("frame")]
    public async Task FailureStopsReturnedTakeExactlyOnceAndWritesFailedArtifact(string fault)
    {
        var r=await Measure("1-3",fault:fault);Assert.Equal(1,r.Code);Assert.Equal(1,r.Starts);Assert.Equal(new[]{"take"},r.Stops);
        Assert.Empty(r.Out);Assert.False(File.Exists(Path.Combine(r.Dir,"measure.json")));
        var failed=JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Dir,"failed.json"))).RootElement;
        Assert.Equal("Failed",failed.GetProperty("state").GetString());Assert.Equal("take",failed.GetProperty("takeId").GetString());
        if(fault=="frame")Assert.Contains("injected frame failure",r.Error);
        if(fault=="cancel")Assert.True(r.Steps.Count<6);
    }
    [Fact] public async Task ExistingOutputDirectoryRefusesBeforeStart()
    {var r=await Measure("1",existing:true);Assert.Equal(1,r.Code);Assert.Equal(0,r.Starts);Assert.Empty(r.Steps);Assert.Contains("already exists",r.Error);}
    [Fact] public async Task WrongValuedCapabilityRefusesBeforeStart()
    {var r=await Measure("1",stampValue:"measure-v2");Assert.Equal(1,r.Code);Assert.Equal(0,r.Starts);Assert.Empty(r.Steps);Assert.False(Directory.Exists(r.Dir));Assert.Contains("measure",r.Error);}
    [Fact] public async Task ReportMetadataHashesReceiptAndNumericHorizonWitnessActualArtifacts()
    {
        var r=await Measure("1-3",hinted:true);Assert.Equal(0,r.Code);var j=JsonDocument.Parse(r.Out).RootElement;
        string Hash(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        Assert.Equal(Hash(r.Input),j.GetProperty("authoredTimelineSha256").GetString());
        Assert.Equal(Hash(Path.Combine(r.Dir,"timeline.json")),j.GetProperty("timelineSha256").GetString());
        Assert.Equal(Hash(Path.Combine(r.Dir,"manifest.json")),j.GetProperty("manifestSha256").GetString());
        Assert.Equal("1.2",j.GetProperty("contract").GetString());Assert.Equal(6,j.GetProperty("fps").GetInt32());Assert.Equal(64,j.GetProperty("width").GetInt32());Assert.Equal(64,j.GetProperty("height").GetInt32());
        foreach(var row in j.GetProperty("rows").EnumerateArray()){Assert.Equal(JsonValueKind.Number,row.GetProperty("horizonRoll").ValueKind);Assert.Equal(0,row.GetProperty("horizonRoll").GetDouble());}
        var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Dir,"receipt.json"))).RootElement;
        Assert.Equal(3,receipt.GetProperty("measures").GetArrayLength());Assert.Equal(JsonValueKind.Array,receipt.GetProperty("reviews").ValueKind);
        Assert.Contains("capturing measure-cli:",r.Error);
    }
    [Fact] public async Task CompositionRetentionComparesEngineRowToPersistedReport()
    {
        var r=await Measure("1",retention:true);Assert.Equal(0,r.Code);
        var original=Assert.Single(r.Rows).GetProperty("composition").GetRawText();
        var stored=JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Dir,"measure.json"))).RootElement.GetProperty("rows")[0].GetProperty("composition").GetRawText();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original),JsonNode.Parse(stored)));Assert.Contains("engine note",stored);
    }
    [Theory] [InlineData("short")] [InlineData("time")]
    public async Task MalformedCoverageOrTimeCannotProduceMeasureReport(string skew)
    {var r=await Measure("1-3",skew:skew);Assert.Equal(1,r.Code);Assert.Contains(skew=="short"?"requested frames":"disagrees with FrameClock",r.Error);Assert.False(File.Exists(Path.Combine(r.Dir,"measure.json")));}
}
