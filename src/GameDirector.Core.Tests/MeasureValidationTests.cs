using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class MeasureValidationTests
{
    private static TimelineAsset Timeline()=>new(){Cues=new(){new(){Type="camera.shot",Shot=new(){Subject="hero",From="front"}}}};
    private static MeasureSample Sample()=>new(){Frame=6,Time=1,Camera=new(){Position=new[]{0d,1,3},Right=new[]{1d,0,0},Up=new[]{0d,1,0},Forward=new[]{0d,0,-1},Fov=50,Aspect=1},Composition=new(){Active=true,Subject="hero",Center=new[]{.5,.5,3},Roles=new(){new(){Role="hero",Kind="subject",State="in-front",Rect=new[]{.2,.2,.8,.8},Crown=new[]{.5,.8,3}}}}};
    private static IReadOnlyList<MeasureRow> Read(JsonNode node)=>MeasureRecorder.ReadMeasures(new[]{JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()},Timeline(),new[]{0},new[]{6},6);
    private static (JsonObject Parent,string Key) Property(JsonNode root,string path)
    {
        var parts=path.Split('.');JsonNode node=root;
        foreach(var part in parts[..^1])node=int.TryParse(part,out var index)?node[index]!:node[part]!;
        return (node.AsObject(),parts[^1]);
    }
    public static IEnumerable<object[]> MalformedFields()
    {
        foreach(var path in new[]{"frame","time","camera","composition","camera.position","camera.right","camera.up","camera.forward","camera.fov","camera.aspect","composition.active","composition.subject","composition.roles","composition.roles.0.role","composition.roles.0.kind","composition.roles.0.state"})
            foreach(var kind in new[]{"null","missing"})yield return new object[]{path,kind};
        foreach(var path in new[]{"camera.position","camera.right","camera.up","camera.forward","composition.center","composition.roles.0.crown"})
            foreach(var kind in new[]{"empty","two","four","type"})yield return new object[]{path,kind};
        foreach(var kind in new[]{"empty","two","three","type"})yield return new object[]{"composition.roles.0.rect",kind};
        foreach(var path in new[]{"composition.roles.0.kind","composition.roles.0.state"})yield return new object[]{path,"vocabulary"};
        yield return new object[]{"composition.roles.0","null-role"};yield return new object[]{"composition.active","type"};
    }
    // Deleting any required camera/role/shape guard must permit a specific malformed row or leak a non-protocol exception.
    [Theory] [MemberData(nameof(MalformedFields))]
    public void MalformedFieldsAreEngineProtocolNotNullOrInvalidOperationCrashes(string path,string kind)
    {
        var node=JsonNode.Parse(DslJson.Serialize(Sample()))!;
        if(kind=="null-role")node["composition"]!["roles"]![0]=null;
        else{
            var (parent,key)=Property(node,path);
            if(kind=="missing")parent.Remove(key);
            else parent[key]=kind switch{"null"=>null,"empty"=>new JsonArray(),"two"=>new JsonArray(0,1),"three"=>new JsonArray(0,1,2),"four"=>new JsonArray(0,1,2,3),_=>JsonValue.Create("outside-vocabulary")};
        }
        Assert.Throws<EngineProtocolException>(()=>Read(node));
    }
    public static IEnumerable<object[]> NonFiniteFields()
    {
        foreach(var value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity}){
            foreach(var path in new[]{"time","fov","aspect"})yield return new object[]{path,0,value};
            foreach(var (path,count) in new[]{("position",3),("right",3),("up",3),("forward",3),("center",3),("rect",4),("crown",3)})
                for(int i=0;i<count;i++)yield return new object[]{path,i,value};
        }
    }
    [Theory] [MemberData(nameof(NonFiniteFields))]
    public void EveryNonFiniteCoordinateIsRejectedDirectlyByCore(string path,int index,double value)
    {
        var s=Sample();IReadOnlyList<double> Change(IReadOnlyList<double> v){var a=v.ToArray();a[index]=value;return a;}
        switch(path){case "time":s.Time=value;break;case "fov":s.Camera.Fov=value;break;case "aspect":s.Camera.Aspect=value;break;
            case "position":s.Camera.Position=Change(s.Camera.Position);break;case "right":s.Camera.Right=Change(s.Camera.Right);break;case "up":s.Camera.Up=Change(s.Camera.Up);break;case "forward":s.Camera.Forward=Change(s.Camera.Forward);break;
            case "center":s.Composition.Center=Change(s.Composition.Center);break;case "rect":s.Composition.Roles[0].Rect=Change(s.Composition.Roles[0].Rect);break;case "crown":s.Composition.Roles[0].Crown=Change(s.Composition.Roles[0].Crown);break;}
        Assert.Throws<ArgumentException>(()=>MeasureEvaluation.Evaluate(new[]{s},Timeline(),new[]{0},new[]{6},6));
    }
    [Fact] public void InactiveFrozenPoseKeepsGoverningCueProvenance()
    {
        var s=Sample();s.Composition=new(){Active=false};var row=Assert.Single(Read(JsonNode.Parse(DslJson.Serialize(s))!));
        Assert.False(row.Composition.Active);Assert.Equal("hero",row.Shot.Subject);Assert.Equal("front",row.Shot.From);Assert.Equal(s.Camera.Position,row.Camera.Position);
    }
    // Two valid-looking active compositions must not silently name different subjects.
    [Fact] public void ActiveSubjectDisagreementIsEngineProtocol()
    {var s=Sample();s.Composition.Subject="other";Assert.Throws<EngineProtocolException>(()=>Read(JsonNode.Parse(DslJson.Serialize(s))!));}
    [Theory] [InlineData("composition.center")] [InlineData("composition.roles.0.rect")] [InlineData("composition.roles.0.crown")]
    public void OptionalProjectionFieldsRemainOptionalInSharedValidator(string path)
    {
        foreach(var missing in new[]{false,true}){
            var node=JsonNode.Parse(DslJson.Serialize(Sample()))!;var (parent,key)=Property(node,path);if(missing)parent.Remove(key);else parent[key]=null;
            var row=Assert.Single(Read(node));Assert.True(CompositionReview.IsValid(row.Composition));
        }
    }
    [Fact] public void LegacyCompositionIsReviewValidButMeasureRequiresActiveSubjectAndRoles()
    {
        var s=Sample();s.Composition.Roles=null!;var verdict=CompositionReview.Evaluate(new[]{new CompositionSample{Time=1,Composition=s.Composition}},Timeline());
        Assert.Contains(verdict.Findings,f=>f.Code==CompositionCodes.Legacy);Assert.True(CompositionReview.IsValid(s.Composition));
        Assert.Throws<EngineProtocolException>(()=>Read(JsonNode.Parse(DslJson.Serialize(s))!));
    }
}
