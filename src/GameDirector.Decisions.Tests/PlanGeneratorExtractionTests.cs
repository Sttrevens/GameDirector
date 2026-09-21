using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

/// <summary>The extracted IPlanGenerator seam: /api/direct keeps its dedupe,
/// persistence and production behavior while the model conversation is a
/// replaceable responsibility; generation consumes a SELECTED performance-match
/// decision as evidence when the decision layer allows; production never
/// touches the decision provider.</summary>
public class PlanGeneratorExtractionTests
{
    private sealed class CountingGenerator : IPlanGenerator
    {
        public int Calls;
        public PlanGenerationContext? LastContext;
        private readonly Func<PlanGenerationContext, FilmPlan> build;
        public CountingGenerator(Func<PlanGenerationContext, FilmPlan> build) => this.build = build;
        public Task<FilmPlan> Generate(PlanGenerationRequest request, PlanGenerationContext context, CancellationToken ct)
        { Calls++; LastContext = context; return Task.FromResult(build(context)); }
    }

    private static (ApiDirector director, CountingGenerator generator) DirectorRig(Func<PlanGenerationContext, FilmPlan> build)
    {
        var generator = new CountingGenerator(build);
        var director = new ApiDirector(_ => generator);
        director.Configure(new ProviderSettings("http://127.0.0.1:9/v1/chat/completions", "test-model", ""));
        return (director, generator);
    }

    private static JsonNode SavedProposal(ServiceRig rig, string requestId)
    {
        var path = Path.Combine(rig.Studio.StorePath("proposals", "proj"), requestId + ".json");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    [Fact]
    public async Task DirectDedupesAndRejectsChangedInputs()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        var (director, generator) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));
        var request = new DirectRequest("proj", "a short portrait", null, false, "direct-1");
        var first = await director.Direct(request, rig.Studio, rig.Decisions, CancellationToken.None);
        var second = await director.Direct(request, rig.Studio, rig.Decisions, CancellationToken.None);
        Assert.Equal(1, generator.Calls); // persisted proposal replayed, generator not re-asked
        var changed = request with { Brief = "a different story" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => director.Direct(changed, rig.Studio, rig.Decisions, CancellationToken.None));
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task DirectInjectsOnlySelectedDecisionEvidence()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("performance", TestRig.CheerCandidateId, 0.9) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.PerformanceMatch.MinConfidence = 0.5);
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        var (director, generator) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));

        await director.Direct(new DirectRequest("proj", "a beat of relief", null, false, "direct-2"), rig.Studio, rig.Decisions, CancellationToken.None);
        // Only the decision-selected entry enters generation — not the whole
        // catalog, and never an invented semantic claim.
        Assert.NotNull(generator.LastContext!.PerformanceEvidence);
        Assert.Contains(TestRig.CheerCandidateId, generator.LastContext.PerformanceEvidence);
        Assert.Contains("release of tension", generator.LastContext.PerformanceEvidence);
        Assert.Equal(1, provider.Calls);
        // The persisted proposal carries the decision linkage.
        var proposal = SavedProposal(rig, "direct-2");
        Assert.Equal("performance-match", proposal["performanceDecision"]!["task"]!.GetValue<string>());
        Assert.True(proposal["performanceDecision"]!["used"]!.GetValue<bool>());
        var decisionId = proposal["performanceDecision"]!["requestId"]!.GetValue<string>();
        Assert.StartsWith("gen-", decisionId);
        var record = rig.Decisions.ReadRecord("proj", DecisionTasks.PerformanceMatch, decisionId);
        Assert.Equal(DecisionOutcomes.Selected, record.Outcome);

        // Retry the same generation without paying for another decision.
        await director.Direct(new DirectRequest("proj", "a beat of relief", null, false, "direct-2"), rig.Studio, rig.Decisions, CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, generator.Calls);
        // A deliberately new generation ID is a new operation. This also lets
        // a caller retry an uncertain/cancelled prior decision explicitly.
        await director.Direct(new DirectRequest("proj", "a beat of relief", null, false, "direct-2b"), rig.Studio, rig.Decisions, CancellationToken.None);
        Assert.Equal(2, generator.Calls);
        Assert.Equal(2, provider.Calls);
        Assert.NotEqual(decisionId, SavedProposal(rig, "direct-2b")["performanceDecision"]!["requestId"]!.GetValue<string>());
    }

    [Fact]
    public async Task DirectInjectsNothingWhenMatchIsNoMatchOrUncalibrated()
    {
        // No-match: the provider chose none; generation proceeds without claims.
        var noneProvider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("performance", DecisionPolicyBounds.NoneCandidateId, 0.95) });
        await using var rig = await ServiceRig.Start(noneProvider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.PerformanceMatch.MinConfidence = 0.5);
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        var (director, generator) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));
        await director.Direct(new DirectRequest("proj", "a quiet walk", null, false, "direct-nm"), rig.Studio, rig.Decisions, CancellationToken.None);
        Assert.Null(generator.LastContext!.PerformanceEvidence);
        Assert.Equal(1, noneProvider.Calls);
        var linkage = SavedProposal(rig, "direct-nm")["performanceDecision"]!;
        Assert.Equal("no-match", linkage["outcome"]!.GetValue<string>());
        Assert.False(linkage["used"]!.GetValue<bool>());

        // Uncalibrated policy: no automatic selection can feed generation.
        await using var rig2 = await ServiceRig.Start(new TestRig.FakeProvider(Array.Empty<DecisionAnswer>()));
        var (director2, generator2) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));
        rig2.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig2.WriteCatalog(TestRig.Catalog(rig2.ManifestHash));
        await director2.Direct(new DirectRequest("proj", "a quiet walk", null, false, "direct-uc"), rig2.Studio, rig2.Decisions, CancellationToken.None);
        Assert.Null(generator2.LastContext!.PerformanceEvidence);
        Assert.Empty(rig2.GeneratedDecisionIds(DecisionTasks.PerformanceMatch)); // nothing asked, nothing recorded
    }

    [Fact]
    public async Task DirectWithoutDecisionConfigIsUnchanged()
    {
        // No provider configured at all: generation behaves exactly as before
        // the decision layer — no ask, no record, no evidence.
        await using var rig = await ServiceRig.Start(null);
        var (director, generator) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        await director.Direct(new DirectRequest("proj", "a short portrait", null, false, "direct-plain"), rig.Studio, rig.Decisions, CancellationToken.None);
        Assert.Null(generator.LastContext!.PerformanceEvidence);
        Assert.Null(SavedProposal(rig, "direct-plain")["performanceDecision"]);
        Assert.Empty(rig.GeneratedDecisionIds(DecisionTasks.PerformanceMatch));

        // Disabled task: same absence, by explicit policy.
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("performance", TestRig.CheerCandidateId, 0.99) });
        await using var rig2 = await ServiceRig.Start(provider);
        var (director2, generator2) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));
        rig2.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig2.Calibrate(p => { p.PerformanceMatch.Enabled = false; p.PerformanceMatch.MinConfidence = 0.5; });
        rig2.WriteCatalog(TestRig.Catalog(rig2.ManifestHash));
        await director2.Direct(new DirectRequest("proj", "a short portrait", null, false, "direct-off"), rig2.Studio, rig2.Decisions, CancellationToken.None);
        Assert.Null(generator2.LastContext!.PerformanceEvidence);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(rig2.GeneratedDecisionIds(DecisionTasks.PerformanceMatch));
    }

    [Fact]
    public async Task ProductionPathNeverCallsTheDecisionProvider()
    {
        var provider = new TestRig.FakeProvider((_, _) => throw new InvalidOperationException("production must never decide"));
        await using var rig = await ServiceRig.Start(provider);
        var (director, _) = DirectorRig(ctx => FilmCompiler.Starter(ctx.Manifest));
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.PerformanceMatch.MinConfidence = 0.5);
        var result = await director.Direct(new DirectRequest("proj", "a short portrait", null, true, "direct-4"), rig.Studio, rig.Decisions, CancellationToken.None);
        Assert.Equal(0, provider.Calls);
        var job = (ProductionJob)result.GetType().GetProperty("job")!.GetValue(result)!;
        Assert.Equal("Queued", job.State);
        // Direct production submission likewise has no decision dependency.
        var submitted = await rig.Studio.Submit(new ProductionRequest { ProjectId = "proj", RequestId = "plain-1", Film = FilmCompiler.Starter(rig.Manifest) }, CancellationToken.None);
        Assert.Equal("Queued", submitted.State);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task GeneratorRepairsOnceWithValidationFeedback()
    {
        var manifest = TestRig.Manifest(Path.GetTempPath());
        var valid = DslJson.Serialize(FilmCompiler.Starter(manifest));
        var calls = 0;
        var handler = new TestRig.RecordingHandler((request, _) =>
        {
            calls++;
            var content = calls == 1 ? "{\"version\":2}" : valid; // first plan fails validation
            var payload = "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";
            return Task.FromResult(TestRig.Json(payload));
        });
        string? Validate(FilmPlan film)
        {
            try { FilmCompiler.Prepare(film, manifest); return null; } catch (ArgumentException ex) { return ex.Message; }
        }
        var generator = new ChatCompletionsPlanGenerator(new ProviderSettings("http://127.0.0.1:9/v1/chat/completions", "test-model", ""), handler);
        var film = await generator.Generate(new PlanGenerationRequest("portrait", null),
            new PlanGenerationContext(manifest, Array.Empty<MediaAsset>(), null, Validate), CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Correct these validation errors", handler.LastBody);
        Assert.Equal(1, film.Version);
    }

    [Fact]
    public async Task GeneratorPassesThroughModelDeclaredErrors()
    {
        var manifest = TestRig.Manifest(Path.GetTempPath());
        var handler = new TestRig.RecordingHandler(TestRig.Json("""{"choices":[{"message":{"content":"{\"error\":\"the brief needs a crane this stage cannot bind\"}"}}]}"""));
        var generator = new ChatCompletionsPlanGenerator(new ProviderSettings("http://127.0.0.1:9/v1/chat/completions", "test-model", ""), handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => generator.Generate(new PlanGenerationRequest("crane shot", null),
            new PlanGenerationContext(manifest, Array.Empty<MediaAsset>(), null, _ => null), CancellationToken.None));
        Assert.Equal(1, handler.Calls); // declared incapability is not repaired away
    }

    [Fact]
    public async Task GeneratorTimeoutIsBounded()
    {
        var manifest = TestRig.Manifest(Path.GetTempPath());
        var handler = new TestRig.RecordingHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); });
        var generator = new ChatCompletionsPlanGenerator(new ProviderSettings("http://127.0.0.1:9/v1/chat/completions", "test-model", ""), handler, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.Generate(new PlanGenerationRequest("portrait", null),
            new PlanGenerationContext(manifest, Array.Empty<MediaAsset>(), null, _ => null), CancellationToken.None));
    }

    [Fact]
    public void SystemPromptCarriesEvidenceOnlyWhenProvided()
    {
        var manifest = TestRig.Manifest(Path.GetTempPath());
        var example = FilmCompiler.Starter(manifest);
        var with = ChatCompletionsPlanGenerator.SystemPrompt(manifest, Array.Empty<MediaAsset>(), example, "- 8:streamer/Cheer: meaning: relief");
        Assert.Contains("PERFORMANCE EVIDENCE", with);
        Assert.Contains("streamer/Cheer", with);
        var without = ChatCompletionsPlanGenerator.SystemPrompt(manifest, Array.Empty<MediaAsset>(), example, null);
        Assert.DoesNotContain("PERFORMANCE EVIDENCE", without);
    }
}
