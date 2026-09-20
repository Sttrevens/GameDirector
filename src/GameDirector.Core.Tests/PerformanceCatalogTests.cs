using GameDirector.Client;
using Xunit;
namespace GameDirector.Core.Tests;
public class PerformanceCatalogTests {
    [Fact] public void MeaningCannotBeInferredFromAClipName() {
        var m=TestAssets.Manifest(); var a=m.Actors[0];
        var p=new PerformanceEntry{Actor=a.Id,Clip=a.Clips[0],Meaning="happy"};
        var c=new PerformanceCatalog{ManifestSha256="source",Performances=new(){p}};
        Assert.Contains(CatalogTools.Validate(c,m,"source"),e=>e.Contains("needs"));
        p.Evidence.Add(new(){Kind="design",Source="GDD#performance"});
        Assert.Empty(CatalogTools.Validate(c,m,"source"));
        Assert.Contains(CatalogTools.Validate(c,m,"changed"),e=>e.Contains("stale"));
    }
    [Fact] public void UnknownsAreAllowedButUnboundOrUntimedObservationsAreRejected() {
        var m=TestAssets.Manifest();var a=m.Actors[0];
        var p=new PerformanceEntry{Actor=a.Id,Clip=a.Clips[0]};
        var c=new PerformanceCatalog{ManifestSha256="s",Performances=new(){p}};
        Assert.Empty(CatalogTools.Validate(c,m,"s"));
        p.Evidence.Add(new(){Kind="observation",Source="take.mp4"});
        Assert.Contains(CatalogTools.Validate(c,m,"s"),e=>e.Contains("timecodes"));
        p.Evidence[0].Start=0;p.Evidence[0].End=1;p.Clip="not-in-game";
        Assert.Contains(CatalogTools.Validate(c,m,"s"),e=>e.Contains("unbound"));
    }
}
