using System.Globalization;
using System.Security.Cryptography;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
namespace GameDirector.Client;
public sealed record ExpectRunResult(bool Measured,ExpectFile? File=null,ExpectResult? Check=null,string? Note=null);
/// <summary>Client transaction orchestration only; Core owns selection, metric projection and comparison.</summary>
public static class ExpectRecorder
{
    public static ExpectFile Load(string path)
    {var file=DslJson.LoadAuthored<ExpectFile>(path);ExpectEvaluation.Validate(file);return file;}
    public static IReadOnlyList<ExpectSize> Sizes(string? spec,int fps)
    {
        var result=new List<ExpectSize>();
        foreach(var item in (spec??"960x540,540x960").Split(',')){
            var pair=item.Split('x');
            if(pair.Length!=2||!int.TryParse(pair[0],NumberStyles.None,CultureInfo.InvariantCulture,out var width)||!int.TryParse(pair[1],NumberStyles.None,CultureInfo.InvariantCulture,out var height))throw new ArgumentException("--sizes needs WxH,WxH: '"+item+"'");
            var error=CaptureContract.CheckGeometry(fps,width,height);if(error!=null)throw new ArgumentException("--sizes: "+error);
            result.Add(new(){Width=width,Height=height});
        }
        if(result.Select(s=>s.Key()).Distinct().Count()!=result.Count)throw new ArgumentException("--sizes must be unique");return result;
    }
    public static IReadOnlyList<ExpectBeat> Beats(string? spec)
    {
        var result=new List<ExpectBeat>();
        if(!string.IsNullOrEmpty(spec))foreach(var item in spec.Split(',')){
            var pair=item.Split('=');if(pair.Length!=2||!int.TryParse(pair[1],NumberStyles.None,CultureInfo.InvariantCulture,out var frame))throw new ArgumentException("--beats needs label=frame,...: '"+item+"'");
            result.Add(new(){Label=pair[0],Frame=frame});
        }
        ExpectFrames.CheckBeats(result);return result.OrderBy(b=>b.Frame).ThenBy(b=>b.Label,StringComparer.Ordinal).ToArray();
    }
    public static async Task<ExpectRunResult> Record(string timeline,string output,string endpoint,int fps=FilmDefaults.FrameRate,string? sizes=null,string? beats=null,string? work=null,CancellationToken ct=default,Action<string>? progress=null)
    {
        var geometry=Sizes(sizes,fps);
        output=OutputPolicy.Physical(output);if(!output.EndsWith(".expect.json",StringComparison.Ordinal))throw new ArgumentException("--out must be a .expect.json file");
        var labels=beats==null&&File.Exists(output)?Load(output).Beats:Beats(beats);
        var result=await Run(timeline,endpoint,fps,geometry,labels,work,null,output,ct,progress);
        if(result.Measured){Directory.CreateDirectory(Path.GetDirectoryName(output)!);DslJson.SaveAtomic(output,result.File);}
        return new(result.Measured,result.File,Note:result.Note);
    }
    public static async Task<ExpectRunResult> Check(string timeline,string expect,string endpoint,string? work=null,CancellationToken ct=default,Action<string>? progress=null)
    {
        var expected=Load(expect);
        var result=await Run(timeline,endpoint,expected.Fps,expected.Sizes,expected.Beats,work,expected,null,ct,progress);
        return !result.Measured?new(false,Note:result.Note):new(true,Check:ExpectEvaluation.Check(expected,result.File!));
    }
    private sealed record RunResult(bool Measured,ExpectFile? File=null,string? Note=null,string? Root=null);
    private static async Task<RunResult> Run(string timeline,string endpoint,int fps,IReadOnlyList<ExpectSize> sizes,IReadOnlyList<ExpectBeat> beats,string? work,ExpectFile? expected,string? sidecar,CancellationToken ct,Action<string>? progress)
    {
        var bytes=await File.ReadAllBytesAsync(timeline,ct);var asset=DslJson.DeserializeAuthored<TimelineAsset>(System.Text.Encoding.UTF8.GetString(bytes));
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var bridge=new DirectorBridgeClient(endpoint);var manifest=await bridge.GetManifestAsync(ct);
        var refusal=MeasureEvaluation.Refusal(manifest);if(refusal!=null)return new(false,Note:refusal);
        var root=OutputPolicy.ManifestRoot(manifest);
        // A sidecar is explicitly versioned data, not media; permit repositories but never the game's source root.
        if(sidecar!=null&&OutputPolicy.Contains(root,OutputPolicy.Physical(sidecar)))throw new ArgumentException("expect sidecar must be outside the game source project");
        var plans=new List<IReadOnlyList<ExpectFrame>>();
        foreach(var size in sizes){
            var geometry=CaptureContract.CheckGeometry(fps,size.Width,size.Height);if(geometry!=null)throw new ArgumentException(geometry);
            var compiled=TimelineCompiler.Compile(asset,manifest,(double)size.Width/size.Height);if(compiled.HasErrors)throw new ArgumentException(string.Join("\n",compiled.Diagnostics));
            plans.Add(ExpectFrames.Select(AspectHints.Resolve(asset,(double)size.Width/size.Height),compiled.Timeline.Duration,fps,beats));
        }
        if(expected!=null)ExpectEvaluation.CheckLayout(expected,fps,sizes.Select((s,i)=>new ExpectSize{Width=s.Width,Height=s.Height,Rows=plans[i].Select(p=>new ExpectRow{Frame=p.Frame}).ToArray()}).ToArray());
        work=OutputPolicy.RequireOutput(work??Path.Combine(Path.GetTempPath(),"gd-expect-"+Guid.NewGuid().ToString("N")),root);
        if(Directory.Exists(work)||File.Exists(work))throw new ArgumentException("--work already exists; choose a new directory");
        Directory.CreateDirectory(work);var recorded=new List<ExpectSize>();
        for(int i=0;i<sizes.Count;i++){
            var size=sizes[i];var plan=plans[i];
            var result=await MeasureRecorder.Record(timeline,OutputPolicy.Child(work,size.Key()),endpoint,string.Join(",",plan.Select(p=>p.Frame)),fps,size.Width,size.Height,ct,progress);
            if(!result.Measured)return new(false,Note:result.Note);
            if(result.Report!.AuthoredTimelineSha256!=hash||result.Report.Fps!=fps||result.Report.Width!=size.Width||result.Report.Height!=size.Height)throw new EngineProtocolException("expect input/geometry changed while recording");
            recorded.Add(ExpectEvaluation.Record(result.Report,plan));
        }
        var file=new ExpectFile{Timeline=Path.GetFileName(timeline),AuthoredSha256=hash,Fps=fps,Beats=beats.OrderBy(b=>b.Frame).ThenBy(b=>b.Label,StringComparer.Ordinal).ToArray(),Sizes=recorded};
        ExpectEvaluation.Validate(file);return new(true,file,Root:root);
    }
}
