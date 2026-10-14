using GameDirector.Client;
using ModelContextProtocol.Protocol;
using Xunit;
namespace GameDirector.Mcp.Tests;
public class AspectPlayTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"gd-mcp-aspect-"+Guid.NewGuid().ToString("N"));
    [Fact] public async Task PlayRefusesHintsClientSideBeforeOldUnityOrUnreachableBridge()
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"timeline.json");File.WriteAllText(path,"""{"version":"0.1","id":"hint","cues":[{"t":0,"type":"camera.shot","aspect":{},"shot":{"subject":"hero"}}]}""");
        var ex=await Assert.ThrowsAsync<ArgumentException>(()=>DirectorTools.GdPlayTimeline(path,"http://127.0.0.1:1"));Assert.Contains("GD1013",ex.Message);
    }
    [Theory] [InlineData(null,null,false)] [InlineData(960,null,false)] [InlineData(null,960,false)]
    [InlineData(960,540,true)] [InlineData(540,960,true)]
    public async Task ValidateResolvesHintsWithBothExplicitDimensions(int? width,int? height,bool valid)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"timeline.json");var manifest=Path.Combine(root,"manifest.json");
        File.WriteAllText(path,"""{"version":"0.1","id":"hint","cues":[{"t":0,"type":"camera.shot","aspect":{"max":1},"shot":{"subject":"hero","from":"front","durationSeconds":1}},{"t":0,"type":"camera.shot","aspect":{"min":1},"shot":{"subject":"hero","from":"side","durationSeconds":1}}]}""");
        File.WriteAllText(manifest,"""{"manifestVersion":"0.1","roles":[{"id":"hero","defaultActor":"body","presentAtStart":true}],"actors":[{"id":"body","clips":[]}],"locations":[{"id":"front"},{"id":"side"}],"shotTypes":["lockoff"],"frameTypes":["medium"]}""");
        var args=new Dictionary<string,object?>{{"timelinePath",path},{"manifestPath",manifest}};if(width.HasValue)args["width"]=width.Value;if(height.HasValue)args["height"]=height.Value;
        var result=await McpServerFixture.WithClientAsync(c=>c.CallToolAsync(McpToolNames.ValidateTimeline,args).AsTask());Assert.True(result.IsError!=true);
        var text=Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        if(valid) { Assert.StartsWith("VALID",text);Assert.Contains("1 cues",text);Assert.DoesNotContain("GD1013",text); }
        else { Assert.Contains("GD1013",text);Assert.Contains("INVALID",text);Assert.Contains("an explicit capture width and height",text);Assert.DoesNotContain("--width",text); }
    }
    public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}
