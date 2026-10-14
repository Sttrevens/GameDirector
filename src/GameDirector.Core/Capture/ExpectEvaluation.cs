using System;
using System.Collections.Generic;
using System.Linq;
using GameDirector.Core.Dsl;
namespace GameDirector.Core.Capture
{
    /// <summary>One numeric/state evaluator over one metric projection. Source hash is information, not taste.</summary>
    public static class ExpectEvaluation
    {
        public static ExpectSize Record(MeasureReport report,IReadOnlyList<ExpectFrame> plan)
        {
            if(!report.Frames.SequenceEqual(plan.Select(f=>f.Frame))||!report.Rows.Select(r=>r.Frame).SequenceEqual(report.Frames))throw new ArgumentException("measure frames differ from expect plan");
            return new ExpectSize{Width=report.Width,Height=report.Height,Rows=report.Rows.Zip(plan,(row,frame)=>new ExpectRow{Frame=row.Frame,Auto=frame.Auto,Beat=frame.Beat,
                Shot=new ExpectShot{AuthoredCue=row.Shot.AuthoredCue,From=row.Shot.From},Metrics=RegressionMetrics.Project(row,report.Height)}).ToArray()};
        }
        public static void CheckLayout(ExpectFile expected,int fps,IReadOnlyList<ExpectSize> actual)
        {
            if(expected.Fps!=fps)throw new ArgumentException("expect fps changed: recorded "+expected.Fps+", actual "+fps);
            if(!expected.Sizes.Select(s=>s.Key()).OrderBy(k=>k).SequenceEqual(actual.Select(s=>s.Key()).OrderBy(k=>k)))throw new ArgumentException("expect sizes changed: recorded "+string.Join(",",expected.Sizes.Select(s=>s.Key()))+", actual "+string.Join(",",actual.Select(s=>s.Key())));
            foreach(var e in expected.Sizes){var a=actual.Single(s=>s.Key()==e.Key());if(!e.Rows.Select(r=>r.Frame).SequenceEqual(a.Rows.Select(r=>r.Frame)))throw new ArgumentException("expect frames changed at "+e.Key()+": recorded "+string.Join(",",e.Rows.Select(r=>r.Frame))+", actual "+string.Join(",",a.Rows.Select(r=>r.Frame)));}
        }
        public static ExpectResult Check(ExpectFile expected,ExpectFile actual)
        {
            Validate(expected);Validate(actual);CheckLayout(expected,actual.Fps,actual.Sizes);
            var result=new ExpectResult{TimelineChanged=expected.AuthoredSha256!=actual.AuthoredSha256};
            foreach(var size in expected.Sizes){var a=actual.Sizes.Single(s=>s.Key()==size.Key());
                for(int i=0;i<size.Rows.Count;i++){
                    var e=size.Rows[i];var row=a.Rows[i];
                    void Compare(string role,Dictionary<string,ExpectValue> left,Dictionary<string,ExpectValue> right){
                        foreach(var metric in RegressionMetrics.All.Where(m=>m.Role==(role!=null))){
                            ExpectValue before=null,after=null;left?.TryGetValue(metric.Id,out before);right?.TryGetValue(metric.Id,out after);
                            if(before==null&&after==null)continue;
                            double? residual=null;
                            if(before!=null&&after!=null){
                                if(metric.Kind==RegressionKind.Number)residual=Math.Abs(after.Value.Value-before.Value.Value);
                                else if(metric.Kind==RegressionKind.Vector)residual=before.Vector.Zip(after.Vector,(x,y)=>Math.Abs(y-x)).Max();
                                else residual=before.Text==after.Text?0:1;
                            }
                            if(residual.HasValue&&residual.Value<=metric.Tolerance)continue;
                            result.Breaches.Add(new ExpectBreach{Size=size.Key(),Frame=e.Frame.Value,Beat=e.Beat,Auto=e.Auto,AuthoredCue=e.Shot.AuthoredCue.Value,From=e.Shot.From,Role=role,
                                Metric=metric.Id,Expected=before?.Raw(),Actual=after?.Raw(),Residual=residual,Tolerance=metric.Tolerance});
                        }
                    }
                    Compare(null,e.Metrics.Values,row.Metrics.Values);
                    foreach(var role in e.Metrics.Roles.Keys.Union(row.Metrics.Roles.Keys).OrderBy(r=>r,StringComparer.Ordinal)){
                        e.Metrics.Roles.TryGetValue(role,out var before);row.Metrics.Roles.TryGetValue(role,out var after);Compare(role,before,after);
                    }
                }
            }
            return result;
        }
        public static void Validate(ExpectFile file)
        {
            if(file==null||file.Version!=1)throw new ArgumentException("expect version must be 1");
            if(string.IsNullOrWhiteSpace(file.Timeline)||file.AuthoredSha256==null||file.AuthoredSha256.Length!=64||file.AuthoredSha256.Any(c=>!(c>='0'&&c<='9'||c>='a'&&c<='f')))throw new ArgumentException("expect needs timeline filename and authoredSha256 (lowercase SHA256)");
            ExpectFrames.CheckBeats(file.Beats);
            if(file.Sizes==null||file.Sizes.Count==0||file.Sizes.Any(s=>s==null)||file.Sizes.Select(s=>s.Key()).Distinct().Count()!=file.Sizes.Count)throw new ArgumentException("expect sizes must be nonempty and unique");
            foreach(var size in file.Sizes){
                var geometry=CaptureContract.CheckGeometry(file.Fps,size.Width,size.Height);if(geometry!=null)throw new ArgumentException("expect "+geometry);
                if(size.Rows==null||size.Rows.Count==0||size.Rows.Any(r=>r==null||!r.Frame.HasValue||r.Frame<0)||!size.Rows.Select(r=>r.Frame).SequenceEqual(size.Rows.Select(r=>r.Frame).Distinct().OrderBy(f=>f)))throw new ArgumentException("expect frames must be present, nonnegative, sorted and unique");
                foreach(var row in size.Rows){
                    if(row.Shot==null||!row.Shot.AuthoredCue.HasValue||row.Shot.AuthoredCue<0||string.IsNullOrEmpty(row.Shot.From))throw new ArgumentException("expect shot needs authoredCue and from");
                    if(row.Auto!=null&&(row.Auto.Count==0||row.Auto.Any(l=>!ExpectFrames.AutoLabels.Contains(l))||row.Auto.Distinct().Count()!=row.Auto.Count))throw new ArgumentException("expect auto labels invalid");
                    if(row.Beat!=null&&(row.Beat.Count==0||row.Beat.Distinct().Count()!=row.Beat.Count||row.Beat.Any(label=>!file.Beats.Any(b=>b.Label==label&&b.Frame==row.Frame))))throw new ArgumentException("expect beat labels disagree with beats");
                    if(row.Auto==null&&row.Beat==null)throw new ArgumentException("expect row needs auto and/or beat labels");
                    if(row.Metrics?.Values==null||row.Metrics.Roles==null)throw new ArgumentException("expect metrics need values and roles");
                    CheckValues(row.Metrics.Values,false);
                    foreach(var role in row.Metrics.Roles){if(string.IsNullOrWhiteSpace(role.Key))throw new ArgumentException("expect role id is empty");CheckValues(role.Value,true);}
                }
                foreach(var beat in file.Beats)if(!size.Rows.Any(r=>r.Frame==beat.Frame&&r.Beat!=null&&r.Beat.Contains(beat.Label)))throw new ArgumentException("expect beat missing from size "+size.Key());
            }
        }
        private static void CheckValues(Dictionary<string,ExpectValue> values,bool role)
        {
            if(values==null)throw new ArgumentException("expect metric values missing");
            foreach(var item in values){
                var metric=RegressionMetrics.Find(item.Key);var value=item.Value;
                if(metric==null||metric.Role!=role||value==null)throw new ArgumentException("expect unknown metric "+item.Key);
                bool valid=metric.Kind==RegressionKind.Number?value.Value.HasValue&&RegressionMetrics.Finite(value.Value.Value)&&value.Vector==null&&value.Text==null:
                    metric.Kind==RegressionKind.Vector?value.Vector!=null&&value.Vector.Count==3&&value.Vector.All(RegressionMetrics.Finite)&&!value.Value.HasValue&&value.Text==null:
                    RoleReviewVocabulary.States.Contains(value.Text)&&!value.Value.HasValue&&value.Vector==null;
                if(!valid)throw new ArgumentException("expect invalid metric "+item.Key);
            }
            foreach(var metric in RegressionMetrics.All.Where(m=>m.Role==role&&(!role||m.Kind==RegressionKind.State)))if(!values.ContainsKey(metric.Id))throw new ArgumentException("expect required metric "+metric.Id);
        }
    }
}
