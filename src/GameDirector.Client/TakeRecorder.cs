using System.Text.Json.Nodes;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

public static class TakeRecorder
{
    public static async Task<string> Record(string timelinePath,string output,string endpoint,int fps=FilmDefaults.FrameRate,int width=FilmDefaults.Width,int height=FilmDefaults.Height,Action<string>? progress=null,CancellationToken ct=default,string? expectedManifestHash=null,MediaProfile? profile=null)
    {
        profile??=MediaProfile.Default;
        output=OutputPolicy.RequireOutput(output);
        if(Directory.Exists(output))throw new IOException("take directory already exists; choose a new take name or use resume");
        await MediaTools.EnsureEncoding(profile,false,ct);
        var timeline=DslJson.Load<TimelineAsset>(timelinePath);
        using var client=new DirectorBridgeClient(endpoint);
        var manifest=await client.GetManifestAsync(ct);
        OutputPolicy.RequireOutput(output, OutputPolicy.ManifestRoot(manifest));
        var compiled=TimelineCompiler.Compile(timeline,manifest);
        if(compiled.HasErrors || compiled.Diagnostics.Any(d=>d.Code==TimelineCompiler.WRoleNotPresent))throw new InvalidOperationException(string.Join("\n",compiled.Diagnostics));
        var geometryError=CaptureContract.CheckGeometry(fps,width,height);
        if(geometryError!=null || compiled.Timeline.Duration<=0 || compiled.Timeline.Duration>CaptureContract.MaxTakeSeconds)
            throw new ArgumentException("invalid take dimensions, rate or duration"+(geometryError!=null?": "+geometryError:""));
        Directory.CreateDirectory(output);
        using var ownership=TakeJobs.Lock(output);
        if(File.Exists(OutputPolicy.Child(output,"job.json")))throw new IOException("another writer created this job");
        var frames=OutputPolicy.Child(output,"frames");Directory.CreateDirectory(frames);
        DslJson.Save(OutputPolicy.Child(output,"timeline.json"),timeline);
        DslJson.Save(OutputPolicy.Child(output,"manifest.json"),manifest);
        var job=new TakeJob{Endpoint=endpoint,FrameRate=fps,Width=width,Height=height,ExpectedFrames=(int)Math.Ceiling(compiled.Timeline.Duration*fps-1e-8),
            TimelineSha256=MediaTools.Hash(OutputPolicy.Child(output,"timeline.json")),ManifestSha256=MediaTools.Hash(OutputPolicy.Child(output,"manifest.json")),
            SourceFingerprint=manifest.Capabilities!=null && manifest.Capabilities.TryGetValue(CapabilityKeys.PresentationSourceFingerprint,out var fingerprint)?fingerprint:""};
        if(expectedManifestHash!=null && expectedManifestHash!=job.ManifestSha256)throw new InvalidOperationException("live source changed; use a new take instead of resume");
        TakeJobs.Save(output,job);
        TakeReceipt? take=null;
        try {
            take=await client.BeginTakeAsync(new TakeRequest{Timeline=timeline,FrameRate=fps,Width=width,Height=height,SourceFingerprint=job.SourceFingerprint},ct);
            if(take.State!="Capturing" || string.IsNullOrWhiteSpace(take.TakeId) || take.FrameCount!=job.ExpectedFrames || take.FrameRate!=fps || take.Width!=width || take.Height!=height || take.TimelineId!=timeline.Id)throw new InvalidOperationException("invalid start receipt");
            job.TakeId=take.TakeId;job.State="Capturing";TakeJobs.Save(output,job);
            for(int i=0;i<take.FrameCount;i++) {
                var bytes=await client.TakeFrameAsync(new FrameRequest{TakeId=take.TakeId,FrameIndex=i},ct);
                var file=OutputPolicy.Child(frames,$"{i:D06}.png");
                await File.WriteAllBytesAsync(file+".tmp",bytes,ct);File.Move(file+".tmp",file);
                TakeJobs.Checkpoint(output,i);
                if(i%fps==0)progress?.Invoke($"capturing {take.TimelineId}: {i}/{take.FrameCount} frames");
            }
            var receipt=await client.GetTakeAsync(ct);await File.WriteAllTextAsync(OutputPolicy.Child(output,"receipt.json"),receipt,ct);
            ValidateReceipt(receipt,job);
            job.State="FramesComplete";TakeJobs.Save(output,job);
            return await Encode(output,job,progress,ct,profile);
        }catch(Exception ex){
            if(take!=null) {try{await client.CancelTakeAsync(take.TakeId,CancellationToken.None);}catch{}}
            job.State="Interrupted";job.Error=ex.Message;TakeJobs.Save(output,job);
            await File.WriteAllTextAsync(OutputPolicy.Child(output,"failed.json"),DslJson.Serialize(new{state="Failed",error=ex.Message,takeId=take?.TakeId}),CancellationToken.None);throw;
        }
    }
    private static void ValidateReceipt(string receipt,TakeJob job) {
        var final=JsonNode.Parse(receipt)?["take"];
        if(final?["takeId"]?.GetValue<string>()!=job.TakeId || final?["state"]?.GetValue<string>()!="Completed" || final?["capturedFrames"]?.GetValue<int>()!=job.ExpectedFrames)
            throw new InvalidOperationException("incomplete or mismatched take receipt");
    }
    private static async Task<string> Encode(string output,TakeJob job,Action<string>? progress,CancellationToken ct,MediaProfile? profile=null) {
        profile??=MediaProfile.Default;
        await MediaTools.EnsureEncoding(profile,false,ct);
        // Failed encoding attempts are retained. Only a fully verified candidate
        // replaces picture.mp4; captured pixels never need the engine to re-encode.
        var candidate=OutputPolicy.Child(output,"encode-"+Guid.NewGuid().ToString("N")+".mp4");
        await MediaTools.Run("ffmpeg",new[]{"-v","error","-n","-framerate",job.FrameRate.ToString(),"-i",OutputPolicy.Child(output,"frames","%06d.png"),"-frames:v",job.ExpectedFrames.ToString(),"-c:v",profile.VideoCodec,"-preset",profile.Preset,"-crf",profile.Crf.ToString(),"-pix_fmt",profile.PixelFormat,"-movflags",profile.MuxerFlags,candidate},ct);
        await MediaTools.Verify(candidate,job.ExpectedFrames,job.FrameRate,job.Width,job.Height,ct);
        var video=OutputPolicy.Child(output,"picture.mp4");
        if(File.Exists(video))File.Move(video,OutputPolicy.Child(output,"previous-"+Guid.NewGuid().ToString("N")+".mp4"));
        File.Move(candidate,video);
        var timeline=DslJson.Load<TimelineAsset>(OutputPolicy.Child(output,"timeline.json"));
        DslJson.SaveAtomic(OutputPolicy.Child(output,"take.json"),new{version=1,takeId=job.TakeId,timelineId=timeline.Id,state="Verified",frameCount=job.ExpectedFrames,frameRate=job.FrameRate,width=job.Width,height=job.Height,
            duration=(double)job.ExpectedFrames/job.FrameRate,video="picture.mp4",videoSha256=MediaTools.Hash(video),timelineSha256=job.TimelineSha256,manifestSha256=job.ManifestSha256,audio="silent-picture; mix audio in the edit"});
        job.State="Verified";job.Error=null;TakeJobs.Save(output,job);
        progress?.Invoke("verified "+video);return video;
    }
    public static async Task<string> Resume(string output,Action<string>? progress=null,CancellationToken ct=default,MediaProfile? profile=null) {
        output=OutputPolicy.RequireOutput(output);
        OutputPolicy.RequireOutput(output, OutputPolicy.ManifestRoot(DslJson.Load<CapabilityManifest>(OutputPolicy.Child(output,"manifest.json"))));
        using var ownership=TakeJobs.Lock(output);
        var job=TakeJobs.Load(output);
        if(job.NextAttempt!=null) {
            var next=TakeJobs.ResolveAttempt(output,job.NextAttempt);
            if(File.Exists(Path.Combine(next,"job.json")))return await Resume(next,progress,ct);
            // A crash before creating a child job leaves only an unused pointer.
            job.NextAttempt=null;TakeJobs.Save(output,job);
        }
        var receiptPath=OutputPolicy.Child(output,"receipt.json");
        if(TakeJobs.HasCompleteFrames(output,job) && File.Exists(receiptPath)) {
            ValidateReceipt(File.ReadAllText(receiptPath),job);
            if(job.State=="Verified") {
                try {
                    var receipt=JsonNode.Parse(File.ReadAllText(OutputPolicy.Child(output,"take.json"))) ?? throw new System.Text.Json.JsonException("empty derived receipt");
                    var video=OutputPolicy.Child(output,"picture.mp4");
                    if(receipt["state"]?.GetValue<string>()=="Verified" && receipt["takeId"]?.GetValue<string>()==job.TakeId &&
                        receipt["timelineSha256"]?.GetValue<string>()==job.TimelineSha256 && receipt["manifestSha256"]?.GetValue<string>()==job.ManifestSha256 &&
                        receipt["frameCount"]?.GetValue<int>()==job.ExpectedFrames && receipt["frameRate"]?.GetValue<int>()==job.FrameRate &&
                        receipt["width"]?.GetValue<int>()==job.Width && receipt["height"]?.GetValue<int>()==job.Height &&
                        File.Exists(video) && receipt["videoSha256"]?.GetValue<string>()==MediaTools.Hash(video))return video;
                } catch(Exception ex) when(ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidOperationException || ex is FormatException) {
                    // Derived receipt is rebuildable from the verified frame journal.
                }
            }
            return await Encode(output,job,progress,ct,profile);
        }
        if(string.IsNullOrWhiteSpace(job.SourceFingerprint))throw new InvalidOperationException("incomplete job has no source fingerprint; preserve it and create a new take");
        using var client=new DirectorBridgeClient(job.Endpoint);
        var manifest=await client.GetManifestAsync(ct);
        OutputPolicy.RequireOutput(output, OutputPolicy.ManifestRoot(manifest));
        var liveHash=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DslJson.Serialize(manifest)));
        if(Convert.ToHexString(liveHash).ToLowerInvariant()!=job.ManifestSha256)throw new InvalidOperationException("live source changed; create a new take");
        var live=JsonNode.Parse(await client.GetTakeAsync(ct))?["take"];
        if(live?["state"]?.GetValue<string>()=="Capturing") {
            if(job.TakeId==null || live["takeId"]?.GetValue<string>()!=job.TakeId)throw new InvalidOperationException("another or unacknowledged take is active; inspect/stop it explicitly before resume");
            await client.CancelTakeAsync(job.TakeId,ct);
        }
        job.NextAttempt=Path.Combine("attempts",Guid.NewGuid().ToString("N"));TakeJobs.Save(output,job);
        progress?.Invoke("incomplete capture: replaying into a new attempt; earlier frames remain intact");
        return await Record(OutputPolicy.Child(output,"timeline.json"),TakeJobs.ResolveAttempt(output,job.NextAttempt),job.Endpoint,job.FrameRate,job.Width,job.Height,progress,ct,job.ManifestSha256,profile);
    }
}
