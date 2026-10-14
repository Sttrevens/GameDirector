using System.Text.Json;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

public sealed class TakeJob
{
    public int Version { get; set; } = 1;
    public string State { get; set; } = TakeJobStates.Prepared;
    public string Endpoint { get; set; } = "";
    public string TimelineSha256 { get; set; } = "";
    /// <summary>Exact authored file bytes, only when aspect alternates were resolved.</summary>
    public string? AuthoredTimelineSha256 { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string? TakeId { get; set; }
    public int FrameRate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Captured picture frames, numbered from zero in frames.jsonl.</summary>
    public int ExpectedFrames { get; set; }
    public int CaptureFromFrame { get; set; }
    // Decided at record time: keep frames/ after the picture verifies (offline
    // re-encode) or reclaim them (replay after picture loss needs the engine).
    public bool RetainFrames { get; set; }
    public string? NextAttempt { get; set; }
    public string? Error { get; set; }
}
/// <summary>A stored take belongs to another pipeline era: its job journal is
/// unreadable or unsupported, or its recorded inputs no longer match. Callers
/// set the entry aside and record again; every other failure still fails.</summary>
public sealed class TakeCacheIncompatibleException : StoreIntegrityException
{
    public TakeCacheIncompatibleException(string message) : base(message) { }
    public TakeCacheIncompatibleException(string message, Exception inner) : base(message, inner) { }
}
public sealed record FrameCheckpoint(int FrameIndex, string Sha256);
public static class TakeJobs
{
    public static void Save(string root, TakeJob job) {
        var target = OutputPolicy.Child(root,"job.json");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp,DslJson.Serialize(job)); File.Move(temp,target,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public static FileStream Lock(string root) {
        root = OutputPolicy.RequireOutput(root);
        var file = OutputPolicy.RequireOutput(OutputPolicy.Child(root, ".recording"));
        if (!OutputPolicy.Contains(root, file)) throw new StoreIntegrityException("Recording lock escaped the take directory.");
        return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public static TakeJob Load(string root) {
        TakeJob job;
        try { job=DslJson.Load<TakeJob>(OutputPolicy.Child(root,"job.json")); }
        catch(Exception ex) when(ex is IOException or JsonException) { throw new TakeCacheIncompatibleException("unreadable take job; set the entry aside and record again",ex); }
        if(job.Version!=1 || job.ExpectedFrames<=0 || job.CaptureFromFrame<0 || GameDirector.Core.Dsl.CaptureContract.CheckGeometry(job.FrameRate,job.Width,job.Height)!=null)
            throw new TakeCacheIncompatibleException("invalid or unsupported take job");
        string timeline,manifest;
        try { timeline=MediaTools.Hash(OutputPolicy.Child(root,"timeline.json")); manifest=MediaTools.Hash(OutputPolicy.Child(root,"manifest.json")); }
        catch(IOException ex) { throw new TakeCacheIncompatibleException("take inputs unreadable; set the entry aside and record again",ex); }
        if(timeline!=job.TimelineSha256 || manifest!=job.ManifestSha256)
            throw new TakeCacheIncompatibleException("take inputs changed; create a new take");
        return job;
    }
    /// <summary>Semantic identity of the stored manifest. Call only after Load has
    /// verified the manifest's file bytes against the job journal; the identity is
    /// derived from the verified bytes, never stored separately.</summary>
    public static string ManifestIdentity(string root) {
        try { return ContentIdentity.Of(DslJson.Load<CapabilityManifest>(OutputPolicy.Child(root,"manifest.json"))); }
        catch(Exception ex) when(ex is IOException or JsonException) { throw new TakeCacheIncompatibleException("unreadable take manifest; set the entry aside and record again",ex); }
    }
    public static void Checkpoint(string root,int index) {
        var file=OutputPolicy.Child(root,"frames",$"{index:D06}.png");
        File.AppendAllText(OutputPolicy.Child(root,"frames.jsonl"),JsonSerializer.Serialize(new FrameCheckpoint(index,MediaTools.Hash(file)),new JsonSerializerOptions(DslJson.Options){WriteIndented=false})+"\n");
    }
    public static bool HasCompleteFrames(string root,TakeJob job) {
        var path=OutputPolicy.Child(root,"frames.jsonl");
        if(!File.Exists(path))return false;
        var lines=File.ReadAllLines(path);
        if(lines.Length!=job.ExpectedFrames)return false;
        for(int i=0;i<lines.Length;i++) {
            FrameCheckpoint? frame;
            try{frame=JsonSerializer.Deserialize<FrameCheckpoint>(lines[i],DslJson.Options);}catch(JsonException){return false;}
            var file=OutputPolicy.Child(root,"frames",$"{i:D06}.png");
            if(frame==null || frame.FrameIndex!=i || !File.Exists(file) || MediaTools.Hash(file)!=frame.Sha256)return false;
        }
        return true;
    }
    public static string ResolveAttempt(string root,string relative) {
        var full=Path.GetFullPath(OutputPolicy.Child(root,relative));
        var prefix=Path.GetFullPath(OutputPolicy.Child(root,"attempts"))+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.Ordinal))throw new StoreIntegrityException("attempt escapes job directory");
        return full;
    }
}
