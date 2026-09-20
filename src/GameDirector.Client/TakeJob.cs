using System.Text.Json;

namespace GameDirector.Client;

public sealed class TakeJob
{
    public int Version { get; set; } = 1;
    public string State { get; set; } = "Prepared";
    public string Endpoint { get; set; } = "";
    public string TimelineSha256 { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string? TakeId { get; set; }
    public int FrameRate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ExpectedFrames { get; set; }
    public string? NextAttempt { get; set; }
    public string? Error { get; set; }
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
        if (!OutputPolicy.Contains(root, file)) throw new IOException("Recording lock escaped the take directory.");
        return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public static TakeJob Load(string root) {
        var job=DslJson.Load<TakeJob>(OutputPolicy.Child(root,"job.json"));
        if(job.Version!=1 || job.ExpectedFrames<=0 || GameDirector.Core.Dsl.CaptureContract.CheckGeometry(job.FrameRate,job.Width,job.Height)!=null)
            throw new InvalidOperationException("invalid or unsupported take job");
        if(MediaTools.Hash(OutputPolicy.Child(root,"timeline.json"))!=job.TimelineSha256 || MediaTools.Hash(OutputPolicy.Child(root,"manifest.json"))!=job.ManifestSha256)
            throw new InvalidOperationException("take inputs changed; create a new take");
        return job;
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
        if(!full.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidOperationException("attempt escapes job directory");
        return full;
    }
}
