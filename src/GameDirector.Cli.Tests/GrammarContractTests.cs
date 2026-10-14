using System.Globalization;
using System.Reflection;
using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Cli.Tests;

/// <summary>
/// The LLM-facing grammar is a generated contract: it must cover everything
/// Core enforces (cue types, shot vocabulary, defaults, capabilities,
/// diagnostic codes), or it drifts — and drift here produces wrong films.
/// </summary>
public class GrammarContractTests
{
    private static readonly string Grammar = DirectorProduct.Grammar();

    [Fact]
    public void MentionsEveryCueType()
    {
        foreach (var type in CueTypes.All) Assert.Contains(type, Grammar);
    }

    [Fact]
    public void MentionsEveryShotSpecField()
    {
        foreach (var property in typeof(ShotSpec).GetProperties())
            Assert.Contains(Camel(property.Name), Grammar);
    }

    [Fact]
    public void MentionsEveryAuthoredCueField()
    {
        foreach (var property in typeof(Cue).GetProperties())
        {
            if (property.Name is nameof(Cue.SourceIndex)) continue; // compiler bookkeeping, not authored
            if (property.Name is nameof(Cue.Injected)) { Assert.Contains("injected", Grammar); continue; }
            Assert.Contains(Camel(property.Name), Grammar);
        }
    }

    [Fact] public void AspectRangeFieldsAndOptionalWithAreGenerated()
    {
        foreach(var property in typeof(AspectRange).GetProperties())Assert.Contains(Camel(property.Name)+"?",Grammar);
        Assert.Contains("aspect?:{min?,max?}",Grammar);Assert.Contains("with?",Grammar);
        Assert.Contains(TimelineCompiler.EAspectHint,Grammar);
    }

    [Fact]
    public void MentionsEveryShotFrameEaseAndParam()
    {
        foreach (var shot in ShotVocabulary.Shots) Assert.Contains(shot.Id, Grammar);
        foreach (var frame in ShotVocabulary.Frames)
        {
            Assert.Contains(frame.Id, Grammar);
            Assert.Contains(frame.Coverage.ToString("0.###", CultureInfo.InvariantCulture), Grammar);
        }
        foreach (var ease in ShotVocabulary.EaseSpecs) { Assert.Contains(ease.Id, Grammar); Assert.Contains(ease.Expression, Grammar); }
        Assert.Contains("fov " + ShotVocabulary.MinFov + ".." + ShotVocabulary.MaxFov, Grammar);
        foreach (var param in ShotVocabulary.Params)
        {
            Assert.Contains(param.Name, Grammar);
            Assert.Contains(param.Unit, Grammar);
            Assert.Contains(param.Meaning, Grammar);
        }
    }

    [Fact]
    public void MentionsEveryDiagnosticCodeWithItsDeclaredSeverity()
    {
        var codes = 0;
        foreach (var field in typeof(TimelineCompiler).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.GetValue(null) is not string code || !code.StartsWith("GD", StringComparison.Ordinal)) continue;
            // The constant name declares the severity the generator groups by.
            Assert.True(field.Name.StartsWith("E", StringComparison.Ordinal) || field.Name.StartsWith("W", StringComparison.Ordinal),
                "diagnostic constant names must declare severity (E*/W*): " + field.Name);
            Assert.Contains(code, Grammar);
            codes++;
        }
        Assert.True(codes >= 15, "expected the full GD code set, found " + codes);
    }

    public static IEnumerable<object[]> Capabilities()=>CapabilitySpecs.All.Select(s=>new object[]{s.Key});
    [Theory] [MemberData(nameof(Capabilities))]
    public void EveryCapabilityRowPublishesItsValuesMeaningsPolicyAndRules(string key)
    {
        var spec=CapabilitySpecs.Find(key);Assert.Contains(key,Grammar);Assert.Contains(spec.Description,Grammar);
        Assert.Contains("declared by "+spec.Declarer,Grammar);Assert.Contains("undeclared: "+spec.Undeclared,Grammar);
        if(spec.Values!=null)foreach(var value in spec.Values){Assert.Contains(value.Value,Grammar);Assert.Contains(value.Meaning,Grammar);}
        else {Assert.Contains(spec.Kind,Grammar);if(spec.ItemSyntax!=null)Assert.Contains(spec.ItemSyntax,Grammar);}
        foreach(var rule in spec.Rules)Assert.Contains((rule.When??"any declared value")+": "+rule.CueType+" "+rule.Field+" → GD1012 ("+(rule.Remedy??rule.Effect)+")",Grammar);
        Assert.DoesNotContain("Manifest capabilities (undeclared = unrestricted)",Grammar);
    }

    [Fact]
    public void MentionsTheOmittedFieldDefaults()
    {
        var shot = new ShotSpec();
        var cue = new Cue();
        Assert.Contains("shot.type: " + shot.Type, Grammar);
        Assert.Contains("shot.frame: " + shot.Frame, Grammar);
        Assert.Contains("shot.from: " + shot.From, Grammar);
        Assert.Contains("shot.ease: " + shot.Ease, Grammar);
        Assert.Contains("fade: " + cue.Fade.ToString("0.###", CultureInfo.InvariantCulture), Grammar);
        Assert.Contains("speed: " + cue.Speed.ToString("0.###", CultureInfo.InvariantCulture), Grammar);
        Assert.Contains("volume: " + cue.Volume.ToString("0.###", CultureInfo.InvariantCulture), Grammar);
        Assert.Contains("scale: " + cue.Scale.ToString("0.###", CultureInfo.InvariantCulture), Grammar);
        Assert.Contains(ShotVocabulary.CurrentAnchor, Grammar);
    }

    [Fact] public void ReviewGrammarDerivesPresetsAllThresholdsAndCadenceFromCore()
    {
        foreach(var row in SafeArea.Presets){Assert.Contains(row.Key,Grammar);Assert.Contains(string.Join(",",new[]{row.Value.Left,row.Value.Right,row.Value.Top,row.Value.Bottom}.Select(v=>v.ToString(CultureInfo.InvariantCulture))),Grammar);}
        var fraction=CompositionPolicy.Default.SingleSideFraction;
        Assert.Contains(">"+(fraction*100).ToString("0.###",CultureInfo.InvariantCulture)+"% rect-width",Grammar);Assert.Contains("cadence "+ReviewPolicy.IntervalSeconds.ToString(CultureInfo.InvariantCulture)+" s",Grammar);
        Assert.Contains("edge tolerance "+CompositionPolicy.Default.EdgeTolerance.ToString("0.###",CultureInfo.InvariantCulture)+" viewport",Grammar);
        Assert.Contains("safe-area side tolerance "+CompositionPolicy.Default.SafeSideTolerance.ToString("0.###",CultureInfo.InvariantCulture)+" viewport",Grammar);
    }

    // Catch a second, stale policy table in review prose; leave unrelated examples/numbers outside these sections alone.
    [Theory] [InlineData("docs/timeline-format.md","### One timeline, multiple aspect ratios","## Compiler rules")]
    [InlineData("docs/directing-playbook.md","Contract 1.1 adds Hor+","Portability acceptance:")]
    [InlineData("skills/gamedirector/SKILL.md","## Aspect-aware raw timelines","## Produce and inspect")]
    public void ReviewDocsDoNotDuplicateCorePolicyNumbers(string file,string start,string end)
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!File.Exists(Path.Combine(dir.FullName,"GameDirector.sln")))dir=dir.Parent;
        var text=File.ReadAllText(Path.Combine(dir?.FullName??throw new DirectoryNotFoundException("GameDirector.sln"),file));var from=text.IndexOf(start,StringComparison.Ordinal);Assert.True(from>=0,file+" review section missing");var to=text.IndexOf(end,from+start.Length,StringComparison.Ordinal);Assert.True(to>from,file+" review section end missing");
        Assert.DoesNotMatch(@"\.1111|\.0682|\.1911|\b25\s*%|\b1\s+s\b|1\s*秒",text[from..to]);
    }

    [Fact]
    public void NamesPinnedMcpToolsInTheWorkflowSection()
    {
        Assert.Contains(McpToolNames.GetManifest, Grammar);
        Assert.Contains(McpToolNames.ValidateTimeline, Grammar);
        Assert.Contains(McpToolNames.PlayTimeline, Grammar);
        Assert.Contains(McpToolNames.CaptureFrame, Grammar);
        Assert.Contains(McpToolNames.StudioState, Grammar);
        Assert.Contains(McpToolNames.StudioValidate, Grammar);
        Assert.Contains(McpToolNames.StudioProduce, Grammar);
        Assert.Contains(McpToolNames.StudioJobStatus, Grammar);
        Assert.Contains(McpToolNames.DirectorGuide, Grammar);
    }

    [Fact]
    public async Task CliGrammarPrintsTheGeneratedContractVerbatim()
    {
        var stdout = new StringWriter();
        Assert.Equal(0, await Program.Run(new[] { "grammar" }, stdout, new StringWriter(), CancellationToken.None));
        Assert.Equal(DirectorProduct.Grammar(), stdout.ToString());
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
