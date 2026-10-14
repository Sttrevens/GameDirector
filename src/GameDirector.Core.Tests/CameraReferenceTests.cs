using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class CameraReferenceTests
{
    private static double[] View(double pan,double tilt,double roll,double fov,double aspect)=>CameraReference.Viewport(pan,tilt,roll,fov,aspect);
    [Fact] public void ReferenceMatchesPinnedLiteralsAtEveryAspect()
    {
        foreach(var s in EngineContract.Current.Camera.Aim.Samples)foreach(var aspect in new[]{1d,9d/16,16d/9}) {
            var v=View(s.Pan,s.Tilt,s.Roll,s.Fov,aspect);Assert.InRange(Math.Abs(v[0]-(.5+(s.X-.5)/aspect)),0,1e-6);Assert.InRange(Math.Abs(v[1]-s.Y),0,1e-6);
        }
        foreach(var s in EngineContract.Current.Camera.Rig.Samples) {
            var c=EngineContract.Current.Camera;var r=CameraReference.AutoFrameRadius(c.Scene.SubjectSize[1],s.Coverage??ShotVocabulary.Coverage(s.Frame),c.Lens.DefaultFov);
            Assert.InRange(Math.Abs(r-s.Radius3D),0,1e-6);
        }
    }
    [Fact] public void AspectScalingAndPanMirrorsAreMetamorphic()
    {
        foreach(var pan in new[]{5d,10,20})foreach(var aspect in new[]{9d/16,16d/9}) {
            var square=View(pan,0,0,50,1);var v=View(pan,0,0,50,aspect);var mirror=View(-pan,0,0,50,aspect);
            Assert.InRange(Math.Abs(v[0]-.5-(square[0]-.5)/aspect),0,1e-12);Assert.Equal(v[1],square[1]);Assert.InRange(Math.Abs(v[0]+mirror[0]-1),0,1e-12);
        }
    }
    [Fact] public void Contract11OnlyAddsListedMembersToFrozen10()
    {
        var graph=JsonNode.Parse(DslJson.Serialize(EngineContract.Current))!;Assert.Equal(2,graph["version"]!["minor"]!.GetValue<int>());MeasureContractTests.Strip12(graph);
        Assert.True(graph["camera"]!["lens"]!.AsObject().Remove("aspect"));Assert.True(graph["review"]!.AsObject().Remove("roles"));
        var optional=graph["review"]!["optional"]!.AsArray();Assert.Equal(5,optional.Count);Assert.Equal("composition.roles",optional[4]!.GetValue<string>());optional.RemoveAt(4);graph["version"]!["minor"]=0;
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!File.Exists(Path.Combine(dir.FullName,"GameDirector.sln")))dir=dir.Parent;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"docs/contract/engine-contract-1.0.json"))),graph));
    }
    [Fact] public void RoleSamplesPinBoxesCrownAndNearPlaneWithoutCopiedCoordinates()
    {
        var roles=JsonNode.Parse(DslJson.Serialize(EngineContract.Current))!["review"]!["roles"];Assert.NotNull(roles);Assert.Equal("role-bounds-v1",roles!["bounds"]!["id"]!.GetValue<string>());
        var rows=roles["samples"]!.AsArray();Assert.Equal(8,rows.Count);
        foreach(var row in rows.Take(6)) { Assert.Equal("in-front",row!["state"]!.GetValue<string>());Assert.Equal(4,row["rect"]!.AsArray().Count);Assert.Equal(3,row["crown"]!.AsArray().Count); }
        foreach(var sample in EngineContract.Current.Review.Roles.Samples) { var v=CameraReference.Bounds(sample.Min,sample.Max,sample.From,sample.Aim,sample.Fov,sample.Aspect,sample.Pan,sample.Tilt,sample.Roll);
            Assert.Equal(sample.State,v.State);Near(sample.Rect,v.Rect);Near(sample.Crown,v.Crown); }
        Assert.Equal("behind",rows[6]!["state"]!.GetValue<string>());Assert.False(rows[6]!.AsObject().ContainsKey("rect"));Assert.False(rows[7]!.AsObject().ContainsKey("rect"));Assert.Equal("straddles-near",rows[7]!["state"]!.GetValue<string>());
        // An unrotated box projects its crown at top-centre, not its nearer top corners.
        var first=rows[0]!;var size=EngineContract.Current.Camera.Scene.SubjectSize;var distance=Math.Abs(EngineContract.Current.Camera.Scene.From[2]);var tangent=Math.Tan(first["fov"]!.GetValue<double>()*Math.PI/360);
        var view=View(first["pan"]!.GetValue<double>(),0,0,first["fov"]!.GetValue<double>(),first["aspect"]!.GetValue<double>());
        var crown=first["crown"]!.AsArray();Assert.InRange(Math.Abs(view[0]-crown[0]!.GetValue<double>()),0,1e-9);
        Assert.InRange(Math.Abs(crown[1]!.GetValue<double>()-(.5+.5*(size[1]/2)/(distance*Math.Cos(first["pan"]!.GetValue<double>()*Math.PI/180)*tangent))),0,1e-9);
        Assert.NotEqual(first["rect"]![3]!.GetValue<double>(),crown[1]!.GetValue<double>());
    }
    private static void Near(IReadOnlyList<double>? expected,IReadOnlyList<double>? actual)
    {
        if(expected==null) { Assert.Null(actual);return; }Assert.NotNull(actual);Assert.Equal(expected.Count,actual.Count);
        for(int i=0;i<expected.Count;i++)Assert.InRange(Math.Abs(expected[i]-actual[i]),0,1e-9);
    }
    [Fact] public void GeneratedRoleRowsPublishStableNineDecimalCoordinates()
    {
        foreach(var s in EngineContract.Current.Review.Roles.Samples) {
            foreach(var value in (s.Rect??Array.Empty<double>()).Concat(s.Crown).Prepend(s.Aspect))Assert.Equal(Math.Round(value,9),value);
            Assert.True(s.Tolerance>=1e-9);
            var raw=CameraReference.Bounds(s.Min,s.Max,s.From,s.Aim,s.Fov,s.Aspect,s.Pan,s.Tilt,s.Roll);
            // Perturbations much larger than libm's one-ulp drift must not cross
            // a decimal midpoint for these fixed, nonsingular published fixtures.
            foreach(var value in (raw.Rect??Array.Empty<double>()).Concat(raw.Crown)) {
                Assert.Equal(Math.Round(value,9),Math.Round(value-1e-12,9));
                Assert.Equal(Math.Round(value,9),Math.Round(value+1e-12,9));
            }
        }
    }
    [Fact] public void HorPlusRowsAreTheSpecifiedSixReferenceSamples()
    {
        var c=JsonNode.Parse(DslJson.Serialize(EngineContract.Current))!["camera"]!["lens"]!["aspect"];Assert.NotNull(c);Assert.Equal("hor-plus",c!["id"]!.GetValue<string>());
        var rows=EngineContract.Current.Camera.Lens.Aspect.Samples;Assert.Equal(6,rows.Count);
        var expected=new[]{(5f,0f,0f),(10f,0f,0f),(20f,15f,30f)}.SelectMany(a=>new[]{9f/16,16f/9}.Select(aspect=>(a.Item1,a.Item2,a.Item3,aspect)));
        Assert.Equal(expected,rows.Select(s=>(s.Pan,s.Tilt,s.Roll,s.Aspect)));
        foreach(var s in rows) { var v=View(s.Pan,s.Tilt,s.Roll,s.Fov,s.Aspect);Assert.Equal((float)v[0],s.X);Assert.Equal((float)v[1],s.Y);
            foreach(var drift in new[]{-1e-12,1e-12}) { Assert.Equal((float)(v[0]+drift),s.X);Assert.Equal((float)(v[1]+drift),s.Y); } }
        foreach(var s in c["samples"]!.AsArray()) { var v=View(s!["pan"]!.GetValue<double>(),s["tilt"]!.GetValue<double>(),s["roll"]!.GetValue<double>(),s["fov"]!.GetValue<double>(),s["aspect"]!.GetValue<double>());
            Assert.InRange(Math.Abs(v[0]-s["x"]!.GetValue<double>()),0,1e-6);Assert.InRange(Math.Abs(v[1]-s["y"]!.GetValue<double>()),0,1e-6); }
    }
}
