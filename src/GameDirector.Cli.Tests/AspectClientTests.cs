using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Cli.Tests;
public class AspectClientTests : TempPaths
{
    private const string Hinted="""
        {"version":"0.1","id":"formats","cues":[
          {"t":0,"type":"camera.shot","aspect":{"max":1},"shot":{"subject":"hero","from":"front","durationSeconds":2}},
          {"t":0,"type":"camera.shot","aspect":{"min":1},"shot":{"subject":"hero","from":"back","durationSeconds":2}}]}
        """;
    private string Fixture() { var root=TempPath("gd-aspect-cli-");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"timeline.json"),Hinted);
        File.WriteAllText(Path.Combine(root,"manifest.json"),"""{"manifestVersion":"0.1","roles":[{"id":"hero","defaultActor":"body","presentAtStart":true}],"actors":[{"id":"body","clips":[]}],"locations":[{"id":"front"},{"id":"back"}],"shotTypes":["lockoff"],"frameTypes":["medium"]}""");return root; }
    private static async Task<(int Code,string Out,string Err)> Run(params string[] args) { var o=new StringWriter();var e=new StringWriter();var c=await Program.Run(args,o,e);return(c,o.ToString(),e.ToString()); }
    [Theory] [InlineData("validate",false)] [InlineData("compile",false)] [InlineData("compile",true)]
    public async Task HintsRequireBothExplicitSizeFlags(string command,bool take)
    {
        var root=Fixture();foreach(var flags in new[]{Array.Empty<string>(),new[]{"--width","960"},new[]{"--height","540"}}) {
            var r=await Run(new[]{command,Path.Combine(root,"timeline.json"),"--manifest",Path.Combine(root,"manifest.json")}.Concat(take?new[]{"--take"}:Array.Empty<string>()).Concat(flags).ToArray());
            Assert.Equal(1,r.Code);Assert.Contains("GD1013",r.Err+r.Out);Assert.Contains("an explicit capture width and height",r.Err+r.Out);
            if(take)Assert.False(JsonNode.Parse(r.Out)!["admission"]!["ok"]!.GetValue<bool>());
        }
    }
    [Theory] [InlineData("validate")] [InlineData("compile")]
    public async Task ExplicitSizesResolveExactlyOneAlternate(string command)
    {
        var root=Fixture();foreach(var (w,h,from) in new[]{(960,540,"back"),(540,960,"front")}) {
            var r=await Run(command,Path.Combine(root,"timeline.json"),"--manifest",Path.Combine(root,"manifest.json"),"--width",w.ToString(),"--height",h.ToString());Assert.Equal(0,r.Code);
            if(command=="compile") { var cues=JsonNode.Parse(r.Out)!["orderedCues"]!.AsArray();Assert.Single(cues);Assert.Equal(from,cues[0]!["shot"]!["from"]!.GetValue<string>());Assert.DoesNotContain("\"aspect\"",r.Out); }
        }
    }
    [Fact] public async Task PlayRefusesHintsLocallyBeforeEvenAnOldEngineCanSeeThem()
    {
        var root=Fixture();var r=await Run("play",Path.Combine(root,"timeline.json"),"--endpoint","http://127.0.0.1:1");Assert.Equal(1,r.Code);Assert.Contains("GD1013",r.Err);Assert.DoesNotContain("unreachable",r.Err);
    }
}
