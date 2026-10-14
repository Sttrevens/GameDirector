using System.Security.Cryptography;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using GameDirector.Core.Compilation;

namespace GameDirector.Client;

public static class TakeRecorder
{
    public static async Task<string> Record(string timelinePath,string output,string endpoint,int fps=FilmDefaults.FrameRate,int width=FilmDefaults.Width,int height=FilmDefaults.Height,Action<string>? progress=null,CancellationToken ct=default,string? expectedManifestHash=null,MediaProfile? profile=null,bool retainFrames=false,int captureFromFrame=0)
    {
        profile??=MediaProfile.Default;
        output=OutputPolicy.RequireOutput(output);
        if(Directory.Exists(output))throw new StateConflictException("take directory already exists; choose a new take name or use resume");
        await MediaTools.EnsureEncoding(profile,false,ct);
        // The timeline argument is an authored file: strict load, so a misspelled
        // field fails here with its JSON path instead of silently taking a default.
        // (Resume re-reads the stored copy through this same path; it was written
        // from a strictly parsed timeline, so it always passes.)
        var authoredBytes=await File.ReadAllBytesAsync(timelinePath,ct);
        TimelineAsset timeline;
        try {
            // StreamReader preserves LoadAuthored's BOM/encoding behavior, but
            // never reopens the file: parse and provenance share one revision.
            using var reader=new StreamReader(new MemoryStream(authoredBytes));
            timeline=DslJson.DeserializeAuthored<TimelineAsset>(await reader.ReadToEndAsync(ct));
        }catch(AuthoredInputException ex) { throw new AuthoredInputException(timelinePath+": "+ex.Message); }
        using var client=new DirectorBridgeClient(endpoint);
        var manifest=await client.GetManifestAsync(ct);
        OutputPolicy.RequireOutput(output, OutputPolicy.ManifestRoot(manifest));
        // The admission decision is made in Core, before the engine is contacted;
        // the engine re-runs the same pure gate and adds only its live fingerprint.
        var admission=TakeAdmission.Admit(timeline,manifest,fps,width,height,captureFromFrame);
        if(!admission.Ok)throw new ArgumentException(string.Join("\n",admission.Errors));
        var authoredHash=AspectHints.HasHints(timeline)?Convert.ToHexString(SHA256.HashData(authoredBytes)).ToLowerInvariant():null;
        timeline=AspectHints.Resolve(timeline,(double)width/height);
        Directory.CreateDirectory(output);
        using var ownership=TakeJobs.Lock(output);
        if(File.Exists(OutputPolicy.Child(output,"job.json")))throw new StateConflictException("another writer created this job");
        var frames=OutputPolicy.Child(output,"frames");Directory.CreateDirectory(frames);
        DslJson.Save(OutputPolicy.Child(output,"timeline.json"),timeline);
        DslJson.Save(OutputPolicy.Child(output,"manifest.json"),manifest);
        var job=new TakeJob{Endpoint=endpoint,FrameRate=fps,Width=width,Height=height,ExpectedFrames=admission.FrameCount-captureFromFrame,CaptureFromFrame=captureFromFrame,RetainFrames=retainFrames,
            AuthoredTimelineSha256=authoredHash,TimelineSha256=MediaTools.Hash(OutputPolicy.Child(output,"timeline.json")),ManifestSha256=MediaTools.Hash(OutputPolicy.Child(output,"manifest.json")),
            SourceFingerprint=manifest.Capabilities!=null && manifest.Capabilities.TryGetValue(CapabilityKeys.PresentationSourceFingerprint,out var fingerprint)?fingerprint:""};
        if(expectedManifestHash!=null && expectedManifestHash!=ContentIdentity.Of(manifest))throw new StateConflictException("live source changed; use a new take instead of resume");
        TakeJobs.Save(output,job);
        TakeReceipt? take=null;
        try {
            take=await client.BeginTakeAsync(new TakeRequest{Timeline=timeline,FrameRate=fps,Width=width,Height=height,CaptureFromFrame=captureFromFrame,SourceFingerprint=job.SourceFingerprint},ct);
            ValidateStartReceipt(take,admission,captureFromFrame,fps,width,height,timeline.Id);
            job.TakeId=take.TakeId;job.State=TakeJobStates.Capturing;TakeJobs.Save(output,job);
            for(int i=0;i<take.FrameCount;i++) {
                var advance=i<captureFromFrame;
                var bytes=await client.TakeFrameAsync(new FrameRequest{TakeId=take.TakeId,FrameIndex=i,AdvanceOnly=advance},ct);
                if(!advance) {
                    var pictureIndex=i-captureFromFrame;
                    var file=OutputPolicy.Child(frames,$"{pictureIndex:D06}.png");
                    await File.WriteAllBytesAsync(file+".tmp",bytes,ct);File.Move(file+".tmp",file);
                    TakeJobs.Checkpoint(output,pictureIndex);
                }
                if(i%fps==0 || i==take.FrameCount-1)progress?.Invoke($"capturing {take.TimelineId}: rendered {Math.Max(0,i+1-captureFromFrame)} frames, advanced {Math.Min(i+1,captureFromFrame)} ({i+1}/{take.FrameCount} steps)");
            }
            var receipt=await client.GetTakeAsync(ct);await File.WriteAllTextAsync(OutputPolicy.Child(output,"receipt.json"),DslJson.Serialize(receipt),ct);
            ValidateReceipt(receipt,job);
            job.State=TakeJobStates.FramesComplete;TakeJobs.Save(output,job);
            return await Encode(output,job,progress,ct,profile);
        }catch(Exception ex){
            if(take!=null) {try{await client.CancelTakeAsync(take.TakeId,CancellationToken.None);}catch{}}
            job.State=TakeJobStates.Interrupted;job.Error=ex.Message;TakeJobs.Save(output,job);
            await File.WriteAllTextAsync(OutputPolicy.Child(output,"failed.json"),DslJson.Serialize(new{state=TakeStates.Failed,error=ex.Message,takeId=take?.TakeId}),CancellationToken.None);throw;
        }
    }
    // Receipt validation is pure and encoder-free on purpose: the engine/Core
    // skew protocol must be testable on every platform, with or without ffmpeg.
    /// <summary>The engine's start receipt must agree with the Core admission
    /// verdict; a disagreement names every disagreeing field and is never
    /// replayable (EngineProtocolException).</summary>
    internal static void ValidateStartReceipt(TakeReceipt take,AdmissionResult admission,int captureFromFrame,int fps,int width,int height,string timelineId) =>
        RequireReceipt("start receipt", "admission", new[] {
            Difference("state",take.State,TakeStates.Capturing),string.IsNullOrWhiteSpace(take.TakeId)?"takeId is empty":null,
            Difference("frameCount",take.FrameCount,admission.FrameCount),Difference("captureFromFrame",take.CaptureFromFrame,captureFromFrame),
            Difference("frameRate",take.FrameRate,fps),Difference("width",take.Width,width),Difference("height",take.Height,height),Difference("timelineId",take.TimelineId,timelineId)
        });
    internal static string? Difference<T>(string field,T actual,T expected) => EqualityComparer<T>.Default.Equals(actual,expected)?null:field+" "+(actual?.ToString()??"null")+" != {expected} "+(expected?.ToString()??"null");
    internal static void RequireReceipt(string receipt,string expected,IEnumerable<string?> differences,bool stored=false) {
        var fields=differences.Where(d=>d!=null).Select(d=>receipt+" "+d!.Replace("{expected}",expected)).ToArray();
        if(fields.Length==0)return;
        if(stored)throw new StoreIntegrityException("incomplete or mismatched take receipt");
        throw new EngineProtocolException(string.Join("; ",fields));
    }
    internal static void ValidateReceipt(TakeSnapshot receipt,TakeJob job,bool stored=false) {
        var final=receipt.Take;
        RequireReceipt("final receipt","job",final==null?new[]{"take is missing"}:new[] {
            Difference("takeId",final.TakeId,job.TakeId),Difference("state",final.State,TakeStates.Completed),Difference("capturedFrames",final.CapturedFrames,job.ExpectedFrames),
            Difference("frameCount",final.FrameCount,job.ExpectedFrames+job.CaptureFromFrame),Difference("captureFromFrame",final.CaptureFromFrame,job.CaptureFromFrame)
        },stored);
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
        var timeline=StoredJson.Load<TimelineAsset>(OutputPolicy.Child(output,"timeline.json"));
        DslJson.SaveAtomic(OutputPolicy.Child(output,"take.json"),new TakeArtifact{TakeId=job.TakeId,TimelineId=timeline.Id,State=TakeJobStates.Verified,FrameCount=job.ExpectedFrames,CaptureFromFrame=job.CaptureFromFrame,FrameRate=job.FrameRate,Width=job.Width,Height=job.Height,
            Duration=FrameClock.TimeOf(job.ExpectedFrames,job.FrameRate),VideoSha256=MediaTools.Hash(video),TimelineSha256=job.TimelineSha256,ManifestSha256=job.ManifestSha256});
        job.State=TakeJobStates.Verified;job.Error=null;TakeJobs.Save(output,job);
        if(!job.RetainFrames) {
            // Frames exist to make capture resumable and encoding verifiable; once
            // the picture is verified they are dead weight. frames.jsonl stays: it
            // is small provenance. Picture loss now costs a replay, by design.
            var frames=OutputPolicy.Child(output,"frames");
            if(Directory.Exists(frames))Directory.Delete(frames,true);
        }
        progress?.Invoke("verified "+video);return video;
    }
    /// <summary>Recover a persisted take. Order matters: the Verified fast path
    /// (receipt + picture hash) runs first and never reads a frame; only without
    /// it are the frame journal's hashes paid for a re-encode, and only without
    /// complete frames does recovery need the engine for a replay.
    /// Returns the verified picture and whether it was reused untouched.</summary>
    public static async Task<(string Video,bool Reused)> Resume(string output,Action<string>? progress=null,CancellationToken ct=default,MediaProfile? profile=null) {
        output=OutputPolicy.RequireOutput(output);
        OutputPolicy.RequireOutput(output, OutputPolicy.ManifestRoot(StoredJson.Load<CapabilityManifest>(OutputPolicy.Child(output,"manifest.json"))));
        using var ownership=TakeJobs.Lock(output);
        var job=TakeJobs.Load(output);
        if(job.NextAttempt!=null) {
            var next=TakeJobs.ResolveAttempt(output,job.NextAttempt);
            if(File.Exists(Path.Combine(next,"job.json")))return await Resume(next,progress,ct,profile);
            // A crash before creating a child job leaves only an unused pointer.
            job.NextAttempt=null;TakeJobs.Save(output,job);
        }
        if(job.State==TakeJobStates.Verified && TryVerifiedVideo(output,job,out var verifiedVideo)) {
            progress?.Invoke("reused "+verifiedVideo);return (verifiedVideo,true);
        }
        var replayReason=job.State==TakeJobStates.Verified
            ? "verified picture lost or corrupt and no complete retained frame journal to re-encode; replaying requires the engine"
            : "incomplete capture: replaying into a new attempt; earlier frames remain intact";
        var receiptPath=OutputPolicy.Child(output,"receipt.json");
        if(TakeJobs.HasCompleteFrames(output,job) && File.Exists(receiptPath)) {
            ValidateReceipt(StoredJson.Load<TakeSnapshot>(receiptPath),job,stored:true);
            return (await Encode(output,job,progress,ct,profile),false);
        }
        if(string.IsNullOrWhiteSpace(job.SourceFingerprint))throw new StoreIntegrityException("incomplete job has no source fingerprint; preserve it and create a new take");
        using var client=new DirectorBridgeClient(job.Endpoint);
        // Unity admits only take/health routes during capture. Inspect ownership
        // first; only this journal's acknowledged take may be stopped. A replay
        // still requires the live source identity after the stage is released.
        var live=(await client.GetTakeAsync(ct)).Take;
        if(live?.State==TakeStates.Capturing) {
            if(job.TakeId==null || live.TakeId!=job.TakeId)throw new StateConflictException("another or unacknowledged take is active; inspect/stop it explicitly before resume");
            await client.CancelTakeAsync(job.TakeId,ct);
        }
        var manifest=await client.GetManifestAsync(ct);
        OutputPolicy.RequireOutput(output, OutputPolicy.ManifestRoot(manifest));
        if(ContentIdentity.Of(manifest)!=TakeJobs.ManifestIdentity(output))throw new StateConflictException("live source changed; create a new take");
        job.NextAttempt=Path.Combine("attempts",Guid.NewGuid().ToString("N"));TakeJobs.Save(output,job);
        progress?.Invoke(replayReason);
        return (await Record(OutputPolicy.Child(output,"timeline.json"),TakeJobs.ResolveAttempt(output,job.NextAttempt),job.Endpoint,job.FrameRate,job.Width,job.Height,progress,ct,TakeJobs.ManifestIdentity(output),profile,job.RetainFrames,job.CaptureFromFrame),false);
    }
    /// <summary>A verified take's receipt matches the job journal and the picture
    /// it certifies is present and unmodified. Any receipt problem is a miss: the
    /// derived take.json is rebuildable from the verified frame journal.</summary>
    private static bool TryVerifiedVideo(string output,TakeJob job,out string video) {
        video=OutputPolicy.Child(output,"picture.mp4");
        try {
            var artifact=DslJson.Load<TakeArtifact>(OutputPolicy.Child(output,"take.json"));
            return artifact.State==TakeJobStates.Verified && artifact.TakeId==job.TakeId &&
                artifact.TimelineSha256==job.TimelineSha256 && artifact.ManifestSha256==job.ManifestSha256 &&
                artifact.FrameCount==job.ExpectedFrames && artifact.CaptureFromFrame==job.CaptureFromFrame && artifact.FrameRate==job.FrameRate &&
                artifact.Width==job.Width && artifact.Height==job.Height &&
                File.Exists(video) && artifact.VideoSha256==MediaTools.Hash(video);
        } catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException) {
            return false;
        }
    }
}
