using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;

namespace GameDirector.Workbench;

public sealed record ProviderSettings(string Endpoint, string Model, string ApiKey);
public sealed record DirectRequest(string ProjectId, string Brief, FilmPlan? CurrentFilm, bool Render, string RequestId);

/// <summary>Bounds of one model round-trip. Named once so the settings UI, the
/// request validator and the HTTP client all enforce the same conversation.</summary>
public static class ApiDirectorPolicy
{
    public const int MaxBriefChars = 20000;
    public const int MaxModelChars = 200;
    public const int MaxApiKeyChars = 4000;
    public const int TimeoutSeconds = 90;
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const int MaxCompletionTokens = 8000;
    public const int MaxPlanAttempts = 2;
}

/// <summary>Endpoint orchestration for /api/direct: request validation, retry
/// dedupe, proposal persistence and optional production submission. The model
/// conversation itself is delegated to an IPlanGenerator, so HTTP/prompt/repair
/// can be tested and replaced independently of this orchestration.</summary>
public sealed class ApiDirector
{
    private ProviderSettings? settings;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Func<ProviderSettings, IPlanGenerator> generatorFactory;

    public ApiDirector(Func<ProviderSettings, IPlanGenerator>? generatorFactory = null) =>
        this.generatorFactory = generatorFactory ?? (s => new ChatCompletionsPlanGenerator(s));

    public object Status => new { configured = settings != null, endpoint = settings?.Endpoint, model = settings?.Model, keyStorage = "memory-only; cleared when Workbench stops" };
    public object Configure(ProviderSettings input)
    {
        if (input.Endpoint == "" && input.ApiKey == "") { settings = null; return Status; }
        if (!Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var uri) || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || string.IsNullOrWhiteSpace(input.Model) || input.Model.Length > ApiDirectorPolicy.MaxModelChars || input.ApiKey.Length > ApiDirectorPolicy.MaxApiKeyChars)
            throw new ArgumentException("Provide an HTTPS Chat Completions endpoint and a model. HTTP is allowed only for a local model.");
        settings = input; return Status;
    }
    public async Task<object> Direct(DirectRequest request, Studio studio, DecisionService? decisions, CancellationToken ct)
    {
        if (!FilmCompiler.ValidId(request.RequestId) || string.IsNullOrWhiteSpace(request.Brief) || request.Brief.Length > ApiDirectorPolicy.MaxBriefChars) throw new ArgumentException("A brief (up to " + ApiDirectorPolicy.MaxBriefChars + " characters) and a stable requestId are required.");
        await serial.WaitAsync(ct);
        try
        {
            var project = studio.Project(request.ProjectId);
            var directory = studio.StorePath("proposals", project.Id); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, request.RequestId + ".json");
            FilmPlan film; var hash = FilmCompiler.Hash(request);
            if (File.Exists(path))
            {
                var saved = JsonNode.Parse(File.ReadAllText(path))!;
                if (saved["requestHash"]!.GetValue<string>() != hash) throw new InvalidOperationException("This director requestId has different inputs.");
                film = DslJson.Deserialize<FilmPlan>(saved["film"]!.ToJsonString());
            }
            else
            {
                var config = settings ?? throw new InvalidOperationException("Connect a model API in settings first.");
                project = await studio.Connect(project.Id, ct);
                // Bounded decision preparation: when the decision provider is
                // configured and performance matching is enabled and calibrated,
                // one audited performance-match decision (deterministic id from
                // the decision inputs) selects the evidence that enters
                // generation. Unconfigured, disabled, uncalibrated, stale or
                // no-match cases inject no semantic claims, and generation is
                // otherwise unchanged. Production never reaches this path.
                PerformanceEvidencePreparation? preparation = null;
                string? evidence = null;
                if (decisions != null)
                {
                    preparation = await decisions.PreparePerformanceEvidence(project.Id, request.Brief, ct, request.RequestId);
                    evidence = preparation.EvidenceContext;
                }
                var generator = generatorFactory(config);
                string? Validate(FilmPlan candidate)
                {
                    try { FilmCompiler.Prepare(candidate, project.Manifest!); MediaLibrary.Validate(studio, project.Id, candidate); return null; }
                    catch (ArgumentException ex) { return ex.Message; }
                }
                film = await generator.Generate(new PlanGenerationRequest(request.Brief, request.CurrentFilm),
                    new PlanGenerationContext(project.Manifest!, MediaLibrary.List(studio, project.Id).ToArray(), evidence, Validate), ct);
                // Decision linkage travels with the persisted proposal so the
                // evidence behind a generated plan stays auditable.
                object? performanceDecision = preparation?.DecisionRequestId == null ? null : new
                {
                    task = DecisionTasks.PerformanceMatch,
                    requestId = preparation.DecisionRequestId,
                    inputHash = preparation.DecisionInputHash,
                    outcome = preparation.DecisionOutcome,
                    selectedCandidateId = preparation.SelectedCandidateId,
                    used = preparation.EvidenceContext != null,
                };
                DslJson.SaveAtomic(path, new { requestHash = hash, film, model = config.Model, sourceManifestHash = FilmCompiler.Hash(project.Manifest), performanceDecision });
            }
            var job = request.Render ? await studio.Submit(new ProductionRequest { RequestId = request.RequestId, ProjectId = project.Id, Film = film }, ct) : null;
            return new { film, job };
        }
        finally { serial.Release(); }
    }
}
