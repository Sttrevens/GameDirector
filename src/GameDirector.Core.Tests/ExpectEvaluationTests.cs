using GameDirector.Client;
using GameDirector.Core.Capture;
using Xunit;
namespace GameDirector.Core.Tests;
public class ExpectEvaluationTests
{
    private static ExpectFile File(double horizon=0,string sha="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")=>new(){Timeline="film.json",AuthoredSha256=sha,Fps=6,Beats=Array.Empty<ExpectBeat>(),Sizes=new[]{new ExpectSize{Width=960,Height=540,Rows=new[]{new ExpectRow{Frame=0,Auto=new[]{"first","mid","last"},Shot=new(){AuthoredCue=1,From="cam"},Metrics=RegressionMetrics.Project(new(){Camera=new(){Position=new[]{0d,0,0},Fov=50},HorizonRoll=horizon,Composition=new(){Active=false}},540)}}}}};
    // An exact comparator mutation must reject valid noise; a dropped timelineChanged flag loses changed-source information.
    [Fact] public void ResidualAtTolerancePassesAboveFailsAndSourceHashIsInformationOnly()
    {
        var expected=File();var at=ExpectEvaluation.Check(expected,File(1e-6));Assert.True(at.Passed);Assert.False(at.TimelineChanged);
        var above=ExpectEvaluation.Check(expected,File(1e-6+1e-12,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.False(above.Passed);Assert.True(above.TimelineChanged);var b=Assert.Single(above.Breaches);
        Assert.Equal("960x540",b.Size);Assert.Equal(0,b.Frame);Assert.Equal(1,b.AuthoredCue);Assert.Equal("cam",b.From);Assert.Null(b.Role);
        Assert.Equal("horizonRoll",b.Metric);Assert.Equal(1e-6,b.Tolerance);Assert.Equal(1e-6+1e-12,b.Residual);Assert.Equal(0d,b.Expected);Assert.Equal(1e-6+1e-12,b.Actual);
        Assert.True(ExpectEvaluation.Check(expected,File(0,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")).Passed);
    }
    [Theory] [InlineData("fps")] [InlineData("sizes")] [InlineData("frames")]
    public void ComparisonGeometryOrCoverageDriftIsAUsageRefusal(string difference)
    {
        var expected=File();var actual=File();
        if(difference=="fps")actual.Fps=12;else if(difference=="sizes")actual.Sizes[0].Height=960;else actual.Sizes[0].Rows[0].Frame=1;
        var e=Assert.Throws<ArgumentException>(()=>ExpectEvaluation.Check(expected,actual));Assert.Contains(difference,e.Message);
    }
    [Fact] public void VectorUsesMaxComponentResidualAndRoleStatesMissingMetricsAndGeometryAreCompared()
    {
        var expected=File();var actual=File();actual.Sizes[0].Rows[0].Metrics.Values["camera.position"].Vector=new[]{0d,0,2e-6};
        var b=Assert.Single(ExpectEvaluation.Check(expected,actual).Breaches);Assert.Equal("camera.position",b.Metric);Assert.Equal(2e-6,b.Residual);
        var role=new CompositionReport{Active=true,Subject="hero",Roles=new(){new(){Role="hero",Kind="subject",State="in-front",Rect=new[]{.1,.2,.3,.4},Crown=new[]{.2,.4,3}}}};
        expected.Sizes[0].Rows[0].Metrics=RegressionMetrics.Project(new(){Camera=new(){Position=new[]{0d,0,0},Fov=50},Composition=role},540);
        actual=File();role.Roles[0].Rect=null;role.Roles[0].Crown=null;role.Roles[0].State="behind";
        actual.Sizes[0].Rows[0].Metrics=RegressionMetrics.Project(new(){Camera=new(){Position=new[]{0d,0,0},Fov=50},Composition=role},540);
        var breaches=ExpectEvaluation.Check(expected,actual).Breaches;Assert.Equal(6,breaches.Count);Assert.All(breaches,b=>Assert.Equal("hero",b.Role));
        Assert.Contains(breaches,b=>b.Metric=="state"&&b.Tolerance==0&&b.Residual==1);Assert.Contains(breaches,b=>b.Metric=="headroom"&&b.Actual==null);
    }
    [Theory] [InlineData("version")] [InlineData("frame")] [InlineData("metric")] [InlineData("nonfinite")]
    public void SemanticallyMalformedSidecarCannotBeCompared(string fault)
    {
        var f=File();if(fault=="version")f.Version=2;else if(fault=="frame")f.Sizes[0].Rows[0].Frame=null;
        else if(fault=="metric")f.Sizes[0].Rows[0].Metrics.Values["unknown"]=new(){Value=0};else f.Sizes[0].Rows[0].Metrics.Values["horizonRoll"].Value=double.NaN;
        Assert.Throws<ArgumentException>(()=>ExpectEvaluation.Validate(f));
    }
}
