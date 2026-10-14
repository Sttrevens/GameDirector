using System.Security.Cryptography;
using System.Text.Json;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
namespace GameDirector.Client;
public sealed record MeasureRecordResult(bool Measured,MeasureReport? Report=null,string? Note=null);
/// <summary>A take transaction without an encoder: all steps, sparse inspection and sparse retained PNGs.</summary>
public static class MeasureRecorder
{
    public static async Task<MeasureRecordResult> Record(string timelinePath,string output,string endpoint,string spec,int fps=FilmDefaults.FrameRate,int width=FilmDefaults.Width,int height=FilmDefaults.Height,CancellationToken ct=default,Action<string>? progress=null)
    {
        FrameSelection.CheckSyntax(spec);
        output=OutputPolicy.RequireOutput(output);
        if(Directory.Exists(output))throw new StateConflictException("measure directory already exists; choose a new name");
        var authoredBytes=await File.ReadAllBytesAsync(timelinePath,ct);TimelineAsset authored;
        try{using var reader=new StreamReader(new MemoryStream(authoredBytes));authored=DslJson.DeserializeAuthored<TimelineAsset>(await reader.ReadToEndAsync(ct));}
        catch(AuthoredInputException ex){throw new AuthoredInputException(timelinePath+": "+ex.Message);}
        using var client=new DirectorBridgeClient(endpoint);var manifest=await client.GetManifestAsync(ct);
        var refusal=MeasureEvaluation.Refusal(manifest);if(refusal!=null)return new(false,Note:refusal);
        OutputPolicy.RequireOutput(output,OutputPolicy.ManifestRoot(manifest));
        var geometry=CaptureContract.CheckGeometry(fps,width,height);if(geometry!=null)throw new ArgumentException(geometry);
        var compiled=TimelineCompiler.Compile(authored,manifest,(double)width/height);
        if(compiled.HasErrors)throw new ArgumentException(string.Join("\n",compiled.Diagnostics));
        var durationError=TakeAdmission.DurationError(compiled.Timeline.Duration);if(durationError!=null)throw new ArgumentException(durationError);
        var frames=FrameSelection.Parse(spec,FrameClock.FrameCount(compiled.Timeline.Duration,fps));var capture=frames[0];
        var admission=TakeAdmission.Admit(authored,manifest,fps,width,height,capture);
        if(!admission.Ok){
            var first=compiled.Timeline.OrderedCues.FirstOrDefault(c=>c.Type==CueTypes.CameraShot);
            if(first!=null&&first.T>FrameClock.TimeOf(capture,fps)+FrameClock.CueEpsilonSeconds)
                throw new ArgumentException("cannot measure before first camera shot frame "+FrameClock.FirstShowingCue(first.T,fps));
            throw new ArgumentException(string.Join("\n",admission.Errors));
        }
        var resolved=AspectHints.Resolve(authored,(double)width/height,out var mapping);
        Directory.CreateDirectory(output);using var ownership=TakeJobs.Lock(output);
        var pngs=OutputPolicy.Child(output,"frames");Directory.CreateDirectory(pngs);
        DslJson.Save(OutputPolicy.Child(output,"timeline.json"),resolved);DslJson.Save(OutputPolicy.Child(output,"manifest.json"),manifest);
        var job=new TakeJob{FrameRate=fps,Width=width,Height=height,ExpectedFrames=admission.FrameCount-capture,CaptureFromFrame=capture,
            TimelineSha256=MediaTools.Hash(OutputPolicy.Child(output,"timeline.json")),ManifestSha256=MediaTools.Hash(OutputPolicy.Child(output,"manifest.json"))};
        TakeReceipt? take=null;
        try{
            take=await client.BeginTakeAsync(new TakeRequest{Timeline=resolved,FrameRate=fps,Width=width,Height=height,CaptureFromFrame=capture,SourceFingerprint=CapabilitySpecs.Declared(manifest,CapabilityKeys.PresentationSourceFingerprint)},ct);
            TakeRecorder.ValidateStartReceipt(take,admission,capture,fps,width,height,resolved.Id);job.TakeId=take.TakeId;
            var wanted=new HashSet<int>(frames);
            for(int i=0;i<take.FrameCount;i++){
                var bytes=await client.TakeFrameAsync(new FrameRequest{TakeId=take.TakeId,FrameIndex=i,AdvanceOnly=i<capture,Measure=wanted.Contains(i)},ct);
                if(wanted.Contains(i))await File.WriteAllBytesAsync(OutputPolicy.Child(pngs,$"{i:D06}.png"),bytes,ct);
                if(i%fps==0||i==take.FrameCount-1)progress?.Invoke($"capturing {take.TimelineId}: rendered {Math.Max(0,i+1-capture)} frames, advanced {Math.Min(i+1,capture)} ({i+1}/{take.FrameCount} steps)");
            }
            var receipt=await client.GetTakeAsync(ct);await File.WriteAllTextAsync(OutputPolicy.Child(output,"receipt.json"),DslJson.Serialize(receipt),ct);
            TakeRecorder.ValidateReceipt(receipt,job);
            // Evaluate the stored resolved revision, not either authored index space or engine bookkeeping.
            var timeline=StoredJson.Load<TimelineAsset>(OutputPolicy.Child(output,"timeline.json"));
            var report=new MeasureReport{Contract=EngineContract.Current.Version.ToString(),AuthoredTimelineSha256=Convert.ToHexString(SHA256.HashData(authoredBytes)).ToLowerInvariant(),
                TimelineSha256=job.TimelineSha256,ManifestSha256=job.ManifestSha256,Fps=fps,Width=width,Height=height,Frames=frames,Rows=ReadMeasures(receipt.Measures,timeline,mapping,frames,fps)};
            DslJson.SaveAtomic(OutputPolicy.Child(output,"measure.json"),report);return new(true,report);
        }catch(Exception ex){
            if(take!=null){try{await client.CancelTakeAsync(take.TakeId,CancellationToken.None);}catch{}}
            await File.WriteAllTextAsync(OutputPolicy.Child(output,"failed.json"),DslJson.Serialize(new{state=TakeStates.Failed,error=ex.Message,takeId=take?.TakeId}),CancellationToken.None);throw;
        }
    }
    internal static IReadOnlyList<MeasureRow> ReadMeasures(JsonElement[]? raw,TimelineAsset resolved,IReadOnlyList<int> authoredCues,IReadOnlyList<int> frames,int fps)
    {
        try{
            var samples=(raw??Array.Empty<JsonElement>()).Select(r=>{
                // Required wire discriminator cannot be recovered from a default-false CLR bool.
                if(r.ValueKind!=JsonValueKind.Object||!r.TryGetProperty("composition",out var c)||c.ValueKind!=JsonValueKind.Object||
                    !c.TryGetProperty("active",out var active)||active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("measure composition needs an active boolean");
                return DslJson.Deserialize<MeasureSample>(r.GetRawText());
            }).ToArray();
            return MeasureEvaluation.Evaluate(samples,resolved,authoredCues,frames,fps);
        }catch(Exception ex)when(ex is JsonException or ArgumentException){throw new EngineProtocolException("invalid measure receipt: "+ex.Message,ex);}
    }
}
