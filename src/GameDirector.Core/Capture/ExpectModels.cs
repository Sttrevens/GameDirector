using System.Collections.Generic;
namespace GameDirector.Core.Capture
{
    public sealed class ExpectBeat { public string Label{get;set;} public int? Frame{get;set;} }
    public sealed class ExpectFrame { public int Frame{get;set;} public IReadOnlyList<string> Beat{get;set;} public IReadOnlyList<string> Auto{get;set;} }
    public sealed class ExpectValue
    {
        public double? Value{get;set;} public IReadOnlyList<double> Vector{get;set;} public string Text{get;set;} public int? Pixels{get;set;}
        public object Raw()=>Value.HasValue?(object)Value.Value:Vector!=null?(object)Vector:Text;
    }
    public sealed class ExpectMetrics
    {
        public Dictionary<string,ExpectValue> Values{get;set;}=new Dictionary<string,ExpectValue>();
        public Dictionary<string,Dictionary<string,ExpectValue>> Roles{get;set;}=new Dictionary<string,Dictionary<string,ExpectValue>>();
    }
    public sealed class ExpectShot { public int? AuthoredCue{get;set;} public string From{get;set;} }
    public sealed class ExpectRow
    {
        public int? Frame{get;set;} public IReadOnlyList<string> Beat{get;set;} public IReadOnlyList<string> Auto{get;set;}
        public ExpectShot Shot{get;set;} public ExpectMetrics Metrics{get;set;}
    }
    public sealed class ExpectSize
    {
        public int Width{get;set;} public int Height{get;set;} public IReadOnlyList<ExpectRow> Rows{get;set;}
        public string Key()=>Width+"x"+Height;
    }
    public sealed class ExpectFile
    {
        public int Version{get;set;}=1; public string Timeline{get;set;} public string AuthoredSha256{get;set;} public int Fps{get;set;}
        public IReadOnlyList<ExpectBeat> Beats{get;set;} public IReadOnlyList<ExpectSize> Sizes{get;set;}
    }
    public sealed class ExpectBreach
    {
        public string Size{get;set;} public int Frame{get;set;} public IReadOnlyList<string> Beat{get;set;} public IReadOnlyList<string> Auto{get;set;}
        public int AuthoredCue{get;set;} public string From{get;set;} public string Role{get;set;} public string Metric{get;set;}
        public object Expected{get;set;} public object Actual{get;set;} public double? Residual{get;set;} public double Tolerance{get;set;}
    }
    public sealed class ExpectResult
    {
        public bool Passed=>Breaches.Count==0; public bool TimelineChanged{get;set;}
        public List<ExpectBreach> Breaches{get;set;}=new List<ExpectBreach>();
    }
}
