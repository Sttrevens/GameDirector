using System.Collections.Generic;
namespace GameDirector.Core.Capture
{
    public sealed class MeasureCamera
    {
        public IReadOnlyList<double> Position{get;set;} public IReadOnlyList<double> Right{get;set;}
        public IReadOnlyList<double> Up{get;set;} public IReadOnlyList<double> Forward{get;set;}
        public double? Fov{get;set;} public double? Aspect{get;set;}
    }
    public sealed class MeasureSample
    {
        public int? Frame{get;set;} public double? Time{get;set;}
        public MeasureCamera Camera{get;set;} public CompositionReport Composition{get;set;}
    }
    public sealed class MeasureShot
    {
        public int ResolvedCue{get;set;} public int AuthoredCue{get;set;} public double T{get;set;}
        public string Type{get;set;} public string Subject{get;set;} public string From{get;set;}
    }
    public sealed class MeasureRow
    {
        public int Frame{get;set;} public double Time{get;set;} public MeasureCamera Camera{get;set;}
        public double HorizonRoll{get;set;} public CompositionReport Composition{get;set;} public MeasureShot Shot{get;set;}
    }
    public sealed class MeasureReport
    {
        public int Version{get;set;}=1; public string Contract{get;set;}
        public string AuthoredTimelineSha256{get;set;} public string TimelineSha256{get;set;} public string ManifestSha256{get;set;}
        public int Fps{get;set;} public int Width{get;set;} public int Height{get;set;}
        public IReadOnlyList<int> Frames{get;set;} public IReadOnlyList<MeasureRow> Rows{get;set;}
    }
}
