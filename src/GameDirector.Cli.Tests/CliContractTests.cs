using GameDirector.Client;
using GameDirector.Core.Dsl;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GameDirector.Cli.Tests;

/// <summary>
/// The CLI's parsing contract, pinned in-process: strict options, stream
/// routing (results stdout, usage/errors stderr) and exit codes
/// 0 ok / 1 operation failed / 2 usage error.
/// </summary>
public class CliContractTests : TempPaths
{
    private static async Task<(int Code, string Stdout, string Stderr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Program.Run(args, stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task UnknownOptionIsAUsageErrorNamingIt()
    {
        var (code, stdout, stderr) = await Run("validate", "timeline.json", "--manfiest", "m.json");
        Assert.Equal(2, code);
        Assert.Contains("--manfiest", stderr);
        Assert.Empty(stdout);
    }

    [Fact]
    public async Task MisspelledFrameRateCannotSilentlyCaptureAt24Fps()
    {
        // The original drift: --frame-rate 30 was ignored and the take ran at 24 fps.
        var (code, _, stderr) = await Run("take", "timeline.json", "--out", "dir", "--frame-rate", "30");
        Assert.Equal(2, code);
        Assert.Contains("--frame-rate", stderr);
    }

    [Fact]
    public async Task UnknownCommandPrintsUsageToStderr()
    {
        var (code, stdout, stderr) = await Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("gd — GameDirector CLI", stderr);
    }

    [Fact]
    public async Task MissingOptionValueIsAUsageErrorNamingIt()
    {
        var (code, _, stderr) = await Run("manifest", "--endpoint");
        Assert.Equal(2, code);
        Assert.Contains("--endpoint", stderr);
    }

    [Fact]
    public async Task NonIntegerGeometryIsAUsageErrorNamingIt()
    {
        var (code, _, stderr) = await Run("compile", "t.json", "--manifest", "m.json", "--take", "--fps", "abc");
        Assert.Equal(2, code);
        Assert.Contains("--fps", stderr);
    }

    [Fact]
    public async Task MissingRequiredOptionIsAUsageErrorNamingIt()
    {
        var (code, _, stderr) = await Run("capture");
        Assert.Equal(2, code);
        Assert.Contains("--out", stderr);
    }

    [Fact]
    public async Task ExtraPositionalIsAUsageError()
    {
        var (code, _, stderr) = await Run("validate", "a.json", "b.json");
        Assert.Equal(2, code);
        Assert.Contains("b.json", stderr);
    }

    [Fact]
    public async Task HelpPrintsUsageToStdout()
    {
        var (code, stdout, stderr) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("gd — GameDirector CLI", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task ValidateReturnsZeroOnAValidTimeline()
    {
        var dir = Fixtures();
        var (code, stdout, _) = await Run("validate", Path.Combine(dir, "valid.json"), "--manifest", Path.Combine(dir, "manifest.json"));
        Assert.Equal(0, code);
        Assert.Contains("VALID", stdout);
    }

    [Fact]
    public async Task ValidateReturnsOneOnCompilerErrors()
    {
        var dir = Fixtures();
        var (code, _, stderr) = await Run("validate", Path.Combine(dir, "invalid.json"), "--manifest", Path.Combine(dir, "manifest.json"));
        Assert.Equal(1, code);
        Assert.Contains("INVALID", stderr);
    }

    [Fact] public async Task EngineContractExportsTheCoreFrameAndCueTolerances()
    {
        var (code, stdout, stderr) = await Run("contract", "--json");
        Assert.Equal(0, code); Assert.Empty(stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.TryGetProperty("frameClock", out var clock));
        Assert.Equal(FrameClock.AlignmentToleranceFrames, clock.GetProperty("alignmentToleranceFrames").GetDouble());
        Assert.Equal(FrameClock.CueEpsilonSeconds, clock.GetProperty("cueEpsilonSeconds").GetDouble());
    }

    [Fact] public async Task EngineContractExportsTheBridgeErrorProtocol()
    {
        var (code, stdout, stderr) = await Run("contract", "--json"); Assert.Equal(0, code); Assert.Empty(stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("bridge").TryGetProperty("errors", out var errors));
        foreach (var (key, status, errorCode) in new[] { ("invalidRequest", 400, "invalid_request"), ("stateConflict", 409, "state_conflict"),
            ("busy", 503, "busy"), ("timeout", 504, "timeout"), ("internal", 500, "internal") }) {
            Assert.Equal(status, errors.GetProperty(key).GetProperty("status").GetInt32());
            Assert.Equal(errorCode, errors.GetProperty(key).GetProperty("code").GetString());
        }
    }

    [Fact] public async Task EngineContractIsExactlyTheVersionedGoldenGraph()
    {
        var (code, stdout, stderr) = await Run("contract", "--json"); Assert.Equal(0, code); Assert.Empty(stderr);
        var graph = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("gamedirector.engine-contract", graph["schema"]?.GetValue<string>());
        Assert.Equal(new[] { "bridge", "camera", "capabilities", "capture", "frameClock", "measure", "review", "schema", "take", "version" },
            graph.Select(p => p.Key).Order());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(DslJson.Serialize(EngineContract.Current)), graph));
        var filename = "engine-contract-" + EngineContract.Current.Version + ".json";
        Assert.Equal("engine-contract-1.2.json", filename);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs/contract", filename))), graph));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stdout) < 256 * 1024);
        AssertRigTokenPins(graph);
        Assert.DoesNotMatch("contract\\s*=\\s*\"", File.ReadAllText(Path.Combine(RepoRoot(), "src/GameDirector.Cli/Program.cs")));
    }

    [Theory]
    [InlineData("camera.rig.minSubjectHeight","0.10000000149011612")]
    [InlineData("camera.rig.distanceFallback.up","0.20000000298023224")]
    [InlineData("camera.rig.distanceFallback.up","0.25")]
    public async Task RigTokenPinsRejectWidenedFloatsAndTheOtherFallback(string path,string token)
    {
        var (code,stdout,_) = await Run("contract","--json");Assert.Equal(0,code);
        var graph=JsonNode.Parse(stdout)!.AsObject();var parts=path.Split('.');JsonNode parent=graph;
        foreach(var part in parts[..^1])parent=parent[part]!;
        parent[parts[^1]]=JsonNode.Parse(token);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(()=>AssertRigTokenPins(graph));
    }
    private static void AssertRigTokenPins(JsonObject graph)
    {
        foreach(var (path,token) in new[]{("camera.rig.nearClip","0.03"),("camera.rig.farClip","1000"),("camera.rig.minSubjectHeight","0.1"),
            ("camera.rig.degenerateSqrMagnitude","0.01"),("camera.rig.autoFrameFallback.up","0.25"),("camera.rig.distanceFallback.up","0.2"),
            ("camera.rig.autoFrameFallback.back","1"),("camera.rig.distanceFallback.back","1")}) {
            JsonNode node=graph;foreach(var part in path.Split('.'))node=node[part]!;
            Assert.Equal(token,node.ToJsonString());
        }
    }

    [Theory] [InlineData("valid.json", 0)] [InlineData("invalid.json", 1)]
    public async Task EveryAdmissionEnvelopeIncludesItsContractVersion(string input, int expected)
    {
        var dir = Fixtures();
        try {
            var (code, stdout, _) = await Run("compile", Path.Combine(dir, input), "--manifest", Path.Combine(dir, "manifest.json"), "--take");
            Assert.Equal(expected, code); var envelope = JsonNode.Parse(stdout)!.AsObject();
            Assert.Equal(EngineContract.Current.Version.ToString(), envelope["contract"]?.GetValue<string>());
            Assert.Equal(expected == 0 ? new[] { "admission", "contract", "duration", "frameCount", "id", "orderedCues" } : new[] { "admission", "contract", "frameCount" }, envelope.Select(p => p.Key).Order());
            Assert.Equal(new[] { "errors", "ok" }, envelope["admission"]!.AsObject().Select(p => p.Key).Order());
            // title is optional on the wire, but belongs to the successful envelope.
            if (expected == 0) {
                var path = Path.Combine(dir, input); var timeline = JsonNode.Parse(File.ReadAllText(path))!; timeline["title"] = "title"; File.WriteAllText(path, timeline.ToJsonString());
                (_, stdout, _) = await Run("compile", path, "--manifest", Path.Combine(dir, "manifest.json"), "--take");
                Assert.Equal(new[] { "admission", "contract", "duration", "frameCount", "id", "orderedCues", "title" }, JsonNode.Parse(stdout)!.AsObject().Select(p => p.Key).Order());
            }
        } finally { Directory.Delete(dir, true); }
    }

    [Fact] public async Task StrictTimelineParseRefusalAlsoCarriesTheContract()
    {
        var dir = Fixtures();
        try {
            var path = Path.Combine(dir, "valid.json"); File.WriteAllText(path, "{broken");
            var (code, stdout, _) = await Run("compile", path, "--manifest", Path.Combine(dir, "manifest.json"), "--take");
            Assert.Equal(1, code); Assert.Equal(new[] { "admission", "contract", "frameCount" }, JsonNode.Parse(stdout)!.AsObject().Select(p => p.Key).Order());
            Assert.Equal(EngineContract.Current.Version.ToString(), JsonNode.Parse(stdout)!["contract"]?.GetValue<string>());
        } finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("sandring", "9d39ad80ab473d7aae66d4d69681378b8ce86c2ffd39d1ce3549bf0b1059a0b0", "e6849e778344c07d303a21fc58c1e8bf295c5e7077fe4dc1364d430b219c3ddf")]
    [InlineData("wide", "d170f23f880cc755b685a7305fdd697a86a016354079f6d1fc09a62619cb5192", "bd82d97fdf226b4a3ce547ae3318d1eb4caae81703b2b06137141aac8767de38")]
    [InlineData("close", "96dc6e3c3213af3d59b04c58f4da196328998b1654b674e4b406cf0fb2abf642", "2bac3aacad550cd0c39e284a4948e7d5bc88a3878cf5d815a932a4adacf45655")]
    [InlineData("side", "81d3aad2e008f929086f5cb04c4e47b4dd0ac4ffe9ddd2a1a2ecd65be37560fe", "b38755c766f1b66456af12cf023385ceab902627ba9d11a7f93abf76e2e6c6b3")]
    public async Task SampleAndDirectorExampleCompiledBytesStayAtThe11AInputBaseline(string name,string plainHash,string takeHash)
    {
        var dir = Fixtures();
        try {
            var timeline = Path.Combine(dir,"example.json"); var manifest = Path.Combine(dir,"manifest.json"); var fps = 12; var from = 0;
            if(name == "sandring") { timeline=Path.Combine(RepoRoot(),"samples/timelines/sandring/timeline.json");manifest=Path.Combine(RepoRoot(),"adapters/sandring/manifest.json");fps=24; }
            else {
                // The three actual per-shot timelines generated by DirectorExample + FilmCompiler at 11.0.
                var i=Array.IndexOf(new[]{"wide","close","side"},name);from=i*12;
                var titles=new[]{"介绍角色与空间","靠近观察角色的回应","从侧面收束表演"};
                var asset=new TimelineAsset{Id="greeting/"+name,Title=titles[i],Cues=new(){
                    new(){T=0,Type=CueTypes.ActorAnim,Role="lead",Clip="Base Layer.Greet",Fade=0},
                    new(){T=i,Type=CueTypes.CameraShot,Shot=new(){Type="lockoff",Subject="lead",From=name,Frame=i==1?"closeup":"full",DurationSeconds=1}},
                    new(){T=i+1,Type=CueTypes.Marker,Label="shot-end"}}};
                File.WriteAllText(timeline,DslJson.Serialize(asset));
                File.WriteAllText(manifest,DslJson.Serialize(new CapabilityManifest{Roles=new(){new(){Id="lead",DefaultActor="body",PresentAtStart=true}},
                    Actors=new(){new(){Id="body",Clips=new(){"Base Layer.Greet"}}},Locations=new(){new(){Id="wide"},new(){Id="close"},new(){Id="side"}},
                    ShotTypes=new(){"lockoff"},FrameTypes=new(){"full","closeup"}}));
            }
            var (code,stdout,_) = await Run("compile",timeline,"--manifest",manifest);Assert.Equal(0,code);Assert.Equal(plainHash,Hash(stdout));
            (code,stdout,_) = await Run("compile",timeline,"--manifest",manifest,"--take","--fps",fps.ToString(),"--width","640","--height","360","--from-frame",from.ToString());
            Assert.Equal(0,code);
            foreach(var emitted in new[]{stdout,stdout.Replace("\r\n","\n",StringComparison.Ordinal).Replace("\n","\r\n",StringComparison.Ordinal)})
                Assert.Equal(takeHash,Hash(WithoutContractLine(emitted)));
        } finally { Directory.Delete(dir,true); }
    }
    [Fact] public void TakeBytePinRequiresTheContractLineBeforeStrippingIt()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(()=>WithoutContractLine("{\r\n  \"frameCount\": 0\r\n}\r\n"));
    }
    private static string WithoutContractLine(string text)
    {
        var normalized=text.Replace("\r\n","\n",StringComparison.Ordinal);
        var line="  \"contract\": \""+EngineContract.Current.Version+"\",\n";
        Assert.Contains(line,normalized);
        return normalized.Replace(line,"",StringComparison.Ordinal);
    }
    // JSON indentation and StringWriter's terminator are platform-local; byte pins use LF.
    private static string Hash(string text)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n","\n",StringComparison.Ordinal)))).ToLowerInvariant();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameDirector.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("GameDirector.sln");
    }

    [Theory] [InlineData("version")] [InlineData("duplicate")] [InlineData("capability")]
    public async Task ParsedInvalidEngineManifestHasNoAdmissionEnvelope(string kind)
    {
        var dir = Fixtures();
        try
        {
            var path = Path.Combine(dir, "manifest.json");
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            if (kind == "version") manifest["manifestVersion"] = "0.2";
            if (kind == "duplicate") manifest["roles"]!.AsArray().Add(manifest["roles"]![0]!.DeepClone());
            if (kind == "capability") manifest["capabilities"] = new System.Text.Json.Nodes.JsonObject { ["actor.animation.fade"] = "wrong" };
            File.WriteAllText(path, manifest.ToJsonString());
            var (code, stdout, stderr) = await Run("compile", Path.Combine(dir, "valid.json"), "--manifest", path, "--take");
            Assert.Equal(1, code);
            Assert.Empty(stdout);
            Assert.Contains("manifest", stderr);
            Assert.Contains("GD", stderr);
        }
        finally { Directory.Delete(dir, true); }
    }

    private string Fixtures()
    {
        var dir = TempPath("gd-cli-tests-");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
            {"game":"Fixture","gameVersion":"1","manifestVersion":"0.1",
             "roles":[{"id":"hero","defaultActor":"body","presentAtStart":true}],
             "actors":[{"id":"body","clips":["wave"]}],
             "locations":[{"id":"front","position":[0,1,3]}],
             "audio":[],"shotTypes":["lockoff"],"frameTypes":["full"]}
            """);
        File.WriteAllText(Path.Combine(dir, "valid.json"), """
            {"version":"0.1","id":"ok","cues":[
              {"t":0,"type":"camera.shot","shot":{"type":"lockoff","subject":"hero","frame":"full","from":"front","durationSeconds":2}}]}
            """);
        File.WriteAllText(Path.Combine(dir, "invalid.json"), """
            {"version":"0.1","id":"bad","cues":[
              {"t":0,"type":"camera.shot","shot":{"type":"lockoff","subject":"ghost","frame":"full","from":"front","durationSeconds":2}}]}
            """);
        return dir;
    }
}
