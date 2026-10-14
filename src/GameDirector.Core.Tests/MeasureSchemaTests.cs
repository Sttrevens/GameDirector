using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class MeasureSchemaTests
{
    // A wire producer/DTO rename must disagree with the single declared schema, not silently shift basis roles.
    [Fact] public void DslJsonSampleAndCameraPathsWitnessDeclaredSchema()
    {
        var camera=new MeasureCamera{Position=new[]{0d,0,0},Right=new[]{1d,0,0},Up=new[]{0d,1,0},Forward=new[]{0d,0,-1},Fov=50,Aspect=1};
        var sample=new MeasureSample{Frame=0,Time=0,Camera=camera,Composition=new(){Active=false}};
        var actual=Paths(JsonNode.Parse(DslJson.Serialize(sample))!).Order();Assert.Equal(EngineContract.Current.Measure.Fields.Order(),actual);
        Assert.Equal(EngineContract.Current.Measure.Fields.Where(p=>p.StartsWith("camera.")).Select(p=>p[7..]).Order(),Paths(JsonNode.Parse(DslJson.Serialize(camera))!).Order());
    }
    private static IEnumerable<string> Paths(JsonNode node,string prefix="")
    {
        foreach(var pair in node.AsObject()){
            var path=prefix+pair.Key;
            if(path=="composition")yield return path;
            else if(pair.Value is JsonArray a)yield return path+"["+a.Count+"]";
            else if(pair.Value is JsonObject o)foreach(var child in Paths(o,path+"."))yield return child;
            else yield return path;
        }
    }
    [Fact] public void FlagDefaultAndSnapshotJsonNamesWitnessDeclaredProtocolNames()
    {
        var spec=JsonNode.Parse(DslJson.Serialize(EngineContract.Current.Measure))!;
        Assert.Equal("measure",spec["flag"]!.GetValue<string>());Assert.Equal("measures",spec["collection"]!.GetValue<string>());
        var request=JsonNode.Parse(DslJson.Serialize(new FrameRequest{Measure=true}))!;
        var defaults=JsonNode.Parse(DslJson.Serialize(new RequestDefaultsSpec()))!;
        Assert.True(request[spec["flag"]!.GetValue<string>()]!.GetValue<bool>());Assert.False(defaults[spec["flag"]!.GetValue<string>()]!.GetValue<bool>());
        foreach(var dto in new object[]{new TakeSnapshot{Measures=Array.Empty<System.Text.Json.JsonElement>()},new StatusSnapshot{Measures=Array.Empty<System.Text.Json.JsonElement>()}})
            Assert.Empty(JsonNode.Parse(DslJson.Serialize(dto))![spec["collection"]!.GetValue<string>()]!.AsArray());
    }
    [Fact] public void SamplesExportWholeBasisTolerancePairAndCombinedOffsets()
    {
        var spec=JsonNode.Parse(DslJson.Serialize(EngineContract.Current.Measure))!;
        Assert.Contains(spec["samples"]!.AsArray(),s=>s!["pan"]!.GetValue<double>()!=0&&s["tilt"]!.GetValue<double>()!=0&&s["roll"]!.GetValue<double>()!=0);
        foreach(var s in spec["samples"]!.AsArray()){
            Assert.NotNull(s!["tolerance"]);Assert.NotNull(s["liveTolerance"]);
            Assert.InRange(s["tolerance"]!.GetValue<double>(),double.Epsilon,1e-6);
            Assert.Equal(2e-2,s["liveTolerance"]!.GetValue<double>());
            foreach(var axis in new[]{"right","up","forward"})Assert.Equal(3,s[axis]!.AsArray().Count);
        }
    }
    // N2: imposing the reference bound on float32 observations must reject these valid live samples.
    [Fact] public void Float32ObservationsFitLiveToleranceButNeedNotFitReferenceTolerance()
    {
        var samples=JsonNode.Parse(DslJson.Serialize(EngineContract.Current.Measure))!["samples"]!.AsArray();
        var needsLiveTolerance=false;
        foreach(var sample in samples){
            Assert.NotNull(sample!["tolerance"]);Assert.NotNull(sample["liveTolerance"]);
            var reference=sample["tolerance"]!.GetValue<double>();var live=sample["liveTolerance"]!.GetValue<double>();
            foreach(var axis in new[]{"right","up","forward"})foreach(var component in sample[axis]!.AsArray()){
                var value=component!.GetValue<double>();var delta=Math.Abs((double)(float)value-value);
                Assert.InRange(delta,0,live);needsLiveTolerance|=delta>reference;
            }
            var right=sample["right"]!.AsArray().Select(v=>(double)(float)v!.GetValue<double>()).ToArray();
            var horizonDelta=Math.Abs(MeasureEvaluation.HorizonRoll(right)-sample["horizonRoll"]!.GetValue<double>());
            Assert.InRange(horizonDelta,0,live);needsLiveTolerance|=horizonDelta>reference;
        }
        Assert.True(needsLiveTolerance,"float32 observations must exercise the wider live bound, not a double-only fixture");
    }
    [Theory] [InlineData(0)] [InlineData(10)]
    public void ThreeWorldRightProjectsRightInExplicitUnityConventionAndHorizonSignIsInvariant(double roll)
    {
        var from=new[]{0d,0,0};var aim=new[]{0d,0,-1};var right=CameraReference.WorldRight(from,aim,0,0,roll);
        double[] Unity(IEnumerable<double> v){var p=v.ToArray();return new[]{p[0],p[1],-p[2]};}
        var point=aim.Select((v,i)=>v+.2*right[i]).ToArray();
        var projected=CameraReference.Project(Unity(point),Unity(from),Unity(aim),60,1,0,0,roll);
        Assert.True(projected[0]>.5);Assert.InRange(Math.Abs(projected[1]-.5),0,1e-12);
        Assert.Equal(MeasureEvaluation.HorizonRoll(right),MeasureEvaluation.HorizonRoll(Unity(right)));
    }
}
