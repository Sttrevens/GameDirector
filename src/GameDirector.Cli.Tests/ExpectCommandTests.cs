using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Cli.Tests;
public class ExpectCommandTests : TempPaths
{
    private async Task RunFixture(Func<string,string,string,Func<int>,Task> test,bool stamp=true)
    {
        var root=TempPath("gd-expect-cli-");Directory.CreateDirectory(root);var source=Path.Combine(root,"source");Directory.CreateDirectory(source);var input=Path.Combine(root,"film.json");var output=Path.Combine(root,"film.expect.json");
        DslJson.Save(input,new TimelineAsset{Id="expect-cli",Cues=new(){new(){T=0,Type="camera.shot",Shot=new(){Subject="hero",From="front",Frame="full",DurationSeconds=1}}}});
        var manifest=new CapabilityManifest{Roles=new(){new(){Id="hero",DefaultActor="body",PresentAtStart=true}},Actors=new(){new(){Id="body"}},Locations=new(){new(){Id="front",Position=new[]{0f,1,3}}},ShotTypes=new(){"lockoff"},FrameTypes=new(){"full"},Capabilities=new(){[CapabilityKeys.DirectorMode]=DirectorModes.OfflineSandbox,[CapabilityKeys.ProjectSourceRoot]=source}};
        if(stamp)manifest.Capabilities[CapabilityKeys.Measure]=CapabilityKeys.MeasureV1;
        using var server=LoopbackHttp.Start();var rows=new List<object>();TakeRequest? request=null;var frames=0;var starts=0;
        TakeReceipt Receipt()=>new(){TakeId="take",TimelineId="expect-cli",State=frames==6?TakeStates.Completed:TakeStates.Capturing,FrameRate=request!.FrameRate,Width=request.Width,Height=request.Height,FrameCount=6,CaptureFromFrame=0,CapturedFrames=frames};
        var loop=Task.Run(async()=>{try{while(true){var c=await server.Listener.GetContextAsync();byte[] bytes;c.Response.ContentType="application/json";
            switch(c.Request.Url!.AbsolutePath){
                case "/manifest":bytes=Encoding.UTF8.GetBytes(DslJson.Serialize(manifest));break;
                case "/take/start":using(var r=new StreamReader(c.Request.InputStream))request=DslJson.Deserialize<TakeRequest>(await r.ReadToEndAsync());starts++;frames=0;rows.Clear();bytes=Encoding.UTF8.GetBytes(DslJson.Serialize(Receipt()));break;
                case "/take/frame":FrameRequest f;using(var r=new StreamReader(c.Request.InputStream))f=DslJson.Deserialize<FrameRequest>(await r.ReadToEndAsync());frames++;
                    if(f.Measure)rows.Add(new{frame=f.FrameIndex,time=f.FrameIndex/6d,camera=new{position=new[]{0d,1,3},right=new[]{1d,0,0},up=new[]{0d,1,0},forward=new[]{0d,0,-1},fov=50d,aspect=(double)request!.Width/request.Height},composition=new{active=true,subject="hero",roles=new[]{new{role="hero",kind="subject",state="in-front",rect=new[]{.2,.2,.8,.8},crown=new[]{.5,.75,3}}}}});
                    c.Response.ContentType="image/png";bytes=new byte[]{137,80,78,71,13,10,26,10,(byte)f.FrameIndex};break;
                case "/take":bytes=Encoding.UTF8.GetBytes(DslJson.Serialize(new{take=Receipt(),events=Array.Empty<object>(),reviews=Array.Empty<object>(),measures=rows}));break;
                default:bytes=Encoding.UTF8.GetBytes("{}");break;
            }
            c.Response.ContentLength64=bytes.Length;await c.Response.OutputStream.WriteAsync(bytes);c.Response.Close();
        }}catch(Exception e)when(e is HttpListenerException or ObjectDisposedException){}});
        try{await test(input,output,server.Endpoint,()=>starts);}finally{server.Dispose();await loop;}
    }
    private static async Task<(int Code,string Out,string Error)> Command(params string[] args)
    {var o=new StringWriter();var e=new StringWriter();var code=await Program.Run(args,o,e);return(code,o.ToString(),e.ToString());}
    [Fact] public async Task RecordCheckAndRerecordReuseMeasureAndKeepExistingBeats()
    {
        await RunFixture(async(input,output,endpoint,starts)=>{
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(output)!,".git")); // sidecars are versioned data, not media output
            var r=await Command("expect-record",input,"--out",output,"--endpoint",endpoint,"--fps","6","--sizes","64x64,64x128","--beats","arrival=4");Assert.True(r.Code==0,r.Error);
            var file=DslJson.LoadAuthored<ExpectFile>(output);Assert.Equal("film.json",file.Timeline);Assert.Equal(2,file.Sizes.Count);Assert.Equal(new[]{0,2,4,5},file.Sizes[0].Rows.Select(row=>row.Frame!.Value));Assert.Equal("arrival",Assert.Single(file.Beats).Label);
            var before=System.IO.File.ReadAllBytes(output);var check=await Command("expect-check",input,"--expect",output,"--endpoint",endpoint);Assert.Equal(0,check.Code);Assert.True(JsonDocument.Parse(check.Out).RootElement.GetProperty("passed").GetBoolean());Assert.Equal(4,starts());
            r=await Command("expect-record",input,"--out",output,"--endpoint",endpoint,"--fps","6","--sizes","64x64,64x128");Assert.True(r.Code==0,r.Error);Assert.Equal(before,System.IO.File.ReadAllBytes(output));Assert.Equal(6,starts());Assert.Contains("capturing",r.Error);
        });
    }
    [Theory] [InlineData("expect-record")] [InlineData("expect-check")]
    public async Task MissingMeasurementCapabilityRefusesWithoutStartingOrWriting(string command)
    {
        await RunFixture(async(input,output,endpoint,starts)=>{
            if(command=="expect-check")DslJson.Save(output,new ExpectFile{Timeline="film.json",AuthoredSha256=new string('a',64),Fps=6,Beats=Array.Empty<ExpectBeat>(),Sizes=new[]{new ExpectSize{Width=64,Height=64,Rows=new[]{new ExpectRow{Frame=0,Auto=new[]{"first"},Shot=new(){AuthoredCue=0,From="front"},Metrics=RegressionMetrics.Project(new(){Camera=new(){Position=new[]{0d,1,3},Fov=50},Composition=new(){Active=false}},64)}}}}});
            var r=await Command(command,input,command=="expect-record"?"--out":"--expect",output,"--endpoint",endpoint);
            Assert.Equal(1,r.Code);Assert.Equal(0,starts());var j=JsonDocument.Parse(r.Out).RootElement;Assert.False(j.GetProperty("measured").GetBoolean());Assert.StartsWith("cannot measure here:",j.GetProperty("note").GetString());
            if(command=="expect-record")Assert.False(System.IO.File.Exists(output));
        },stamp:false);
    }
    [Fact] public async Task ChangedFramePlanIsUsageRefusalBeforeTakeStart()
    {
        await RunFixture(async(input,output,endpoint,starts)=>{
            var r=await Command("expect-record",input,"--out",output,"--endpoint",endpoint,"--fps","6","--sizes","64x64");Assert.True(r.Code==0,r.Error);
            var t=DslJson.Load<TimelineAsset>(input);t.Cues[0].Shot.DurationSeconds=2;DslJson.Save(input,t);
            r=await Command("expect-check",input,"--expect",output,"--endpoint",endpoint);Assert.Equal(2,r.Code);Assert.Contains("frames changed",r.Error);Assert.Equal(1,starts());
        });
    }
    [Theory] [InlineData("64x")] [InlineData("0x64")] [InlineData("64x64,64x64")]
    public async Task BadSizesAreUsageErrorsBeforeIo(string sizes)
    {var r=await Command("expect-record","missing.json","--out","file.expect.json","--endpoint","http://127.0.0.1:1","--sizes",sizes);Assert.Equal(2,r.Code);Assert.Contains("sizes",r.Error);}
    [Theory] [InlineData("version")] [InlineData("unknown")] [InlineData("nested")]
    public void StrictSidecarLoaderNamesMissingAndUnknownFields(string fault)
    {
        var json=new JsonObject{["version"]=1,["timeline"]="x.json",["authoredSha256"]=new string('a',64),["fps"]=6,["beats"]=new JsonArray(),["sizes"]=new JsonArray()};
        if(fault=="version")json.Remove("version");else if(fault=="unknown")json["unexpected"]=1;else json["beats"]=new JsonArray(new JsonObject{["label"]="x",["frame"]=0,["typo"]=1});
        var e=Assert.Throws<AuthoredInputException>(()=>DslJson.DeserializeAuthored<ExpectFile>(json.ToJsonString()));Assert.Contains(fault=="version"?"version: required":"unknown property",e.Message);
    }
}
