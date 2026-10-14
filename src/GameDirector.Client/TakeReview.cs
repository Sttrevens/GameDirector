using System.Text.Json;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
namespace GameDirector.Client;
public sealed record TakeReviewReport(string Directory,string State,bool Judged,CompositionVerdict? Review=null,string? Note=null);
public static class TakeReview
{
    /// <summary>Read only, following Resume's attempt chain. Never judge a receipt
    /// written before verification, or repair a dangling attempt pointer.</summary>
    public static TakeReviewReport Read(string root,SafeArea? safeArea=null)
    {
        root=Path.GetFullPath(root);var visited=new HashSet<string>(StringComparer.Ordinal);
        while(true) {
            if(!visited.Add(root))throw new StoreIntegrityException("cyclic take attempts");
            var job=TakeJobs.Load(root);
            if(job.NextAttempt!=null) {var next=TakeJobs.ResolveAttempt(root,job.NextAttempt);if(File.Exists(Path.Combine(next,"job.json"))){root=next;continue;}}
            if(job.State!=TakeJobStates.Verified)return new(root,job.State,false,Note:"not Verified; receipt not judged");
            TakeSnapshot receipt;
            try {receipt=StoredJson.Load<TakeSnapshot>(OutputPolicy.Child(root,"receipt.json"));TakeRecorder.ValidateReceipt(receipt,job,stored:true);}
            catch(Exception ex) when(ex is StoreIntegrityException or IOException){return new(root,job.State,false,Note:"receipt not judged: "+ex.Message);}
            var timeline=StoredJson.Load<TimelineAsset>(OutputPolicy.Child(root,"timeline.json"));
            var raw=receipt.Reviews??Array.Empty<JsonElement>();var samples=new List<CompositionSample>();var malformed=new List<CompositionFinding>();
            foreach(var r in raw) {
                try {var sample=DslJson.Deserialize<CompositionSample>(r.GetRawText());if(!Valid(sample))throw new JsonException("malformed or non-finite composition sample");samples.Add(sample);}
                catch(JsonException ex) {
                    int? frame=null;double time=0;if(r.ValueKind==JsonValueKind.Object){if(r.TryGetProperty("frame",out var f)&&f.ValueKind==JsonValueKind.Number&&f.TryGetInt32(out var i))frame=i;if(r.TryGetProperty("time",out var t)&&t.ValueKind==JsonValueKind.Number&&t.TryGetDouble(out var n)&&double.IsFinite(n))time=n;}
                    malformed.Add(new(){Frame=frame,Time=time,Code=CompositionCodes.MalformedSample,Severity=CompositionSeverities.Warning,Note=ex.Message});
                }
            }
            var verdict=CompositionReview.Evaluate(samples,timeline,safeArea);verdict.Findings.AddRange(malformed);
            if(raw.Length==0)verdict.Findings.Add(new(){Code=CompositionCodes.NoReviews,Severity=CompositionSeverities.Warning,Note="receipt has no composition samples"});
            return new(root,job.State,true,verdict);
        }
    }
    private static bool Vector(IReadOnlyList<double>? v,int count)=>v==null||(v.Count==count&&v.All(double.IsFinite));
    private static bool Valid(CompositionSample s)=>double.IsFinite(s.Time)&&s.Frame>=0&&s.Composition!=null&&Vector(s.Composition.Center,3)&&(s.Composition.Roles==null||s.Composition.Roles.All(r=>r!=null&&!string.IsNullOrEmpty(r.Role)&&!string.IsNullOrEmpty(r.Kind)&&!string.IsNullOrEmpty(r.State)&&Vector(r.Rect,4)&&Vector(r.Crown,3)));
}
