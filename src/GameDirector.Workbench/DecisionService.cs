using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;

namespace GameDirector.Workbench;

public sealed record PerformanceMatchRequest(string RequestId, string Intent);
public sealed record CameraCandidateInput(string Id, ShotSpec Camera);
public sealed record CameraSuggestRequest(string ShotId, FilmPlan Film);
public sealed record CameraChoiceRequest(string RequestId, string ShotId, string Intent, string? DocumentId, long? DocumentRevision, FilmPlan? Film, List<CameraCandidateInput>? Candidates);
public sealed record AudioMatchRequest(string RequestId, string Intent, string? Bus);
public sealed record FilmCandidateInput(string Id, string? Rationale, FilmPlan Film);
public sealed record FilmRankingRequest(string RequestId, string Brief, List<FilmCandidateInput> Candidates);
public sealed record SemanticCheckInput(string Id, string Kind, string Question, List<string>? Legend);
public sealed record SemanticReviewRequest(string RequestId, string Subject, string? Label, List<SemanticCheckInput> Checks);

/// <summary>Result of bounded decision preparation for generative planning.
/// EvidenceContext is non-null only when a calibrated performance-match
/// decision selected an evidence-backed catalog entry for this exact intent;
/// the decision linkage identifies the persisted audit record.</summary>
public sealed record PerformanceEvidencePreparation(string? EvidenceContext, string? DecisionRequestId, string? DecisionInputHash, string? DecisionOutcome, string? SelectedCandidateId)
{
    public static readonly PerformanceEvidencePreparation None = new(null, null, null, null, null);
}

/// <summary>Workbench orchestration of the decision layer: provider connection
/// (memory-only key), persisted per-task policies, performance-catalog storage
/// and freshness, and the decision workflows. Every decision attempt commits
/// one audit record; reusing a requestId with identical inputs returns the
/// saved record, with changed inputs it conflicts. Decisions happen only here,
/// during proposal preparation — production never reaches this service.</summary>
public sealed class DecisionService
{
    private readonly Studio studio;
    private DecisionProviderSettings? settings; // memory-only, cleared on restart
    private DecisionPolicies policies;          // persisted; no secrets
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Func<DecisionProviderSettings, IDecisionProvider> providerFactory;

    public DecisionService(Studio studio, Func<DecisionProviderSettings, IDecisionProvider>? providerFactory = null)
    {
        this.studio = studio;
        this.providerFactory = providerFactory ?? (s => new OpenRouterJevProvider(s));
        var path = PoliciesPath();
        policies = File.Exists(path) ? DslJson.Load<DecisionPolicies>(path) : new DecisionPolicies();
        policies.Validate();
    }

    // ---- provider connection + policies ----

    private string PoliciesPath() => studio.StorePath("decisions", "policies.json");

    public object ProviderStatus()
    {
        lock (gate)
            return new
            {
                configured = settings != null,
                endpoint = settings?.Endpoint,
                model = settings?.Model,
                providerId = OpenRouterJevProvider.Id,
                keyStorage = "memory-only; cleared when Workbench stops",
                official = new { endpoint = OpenRouterJevProvider.DefaultEndpoint, model = OpenRouterJevProvider.DefaultModel },
                policies = policies.Clone(),
            };
    }

    public object ConfigureProvider(DecisionProviderSettings input)
    {
        if (input.Endpoint == "" && input.ApiKey == "") { lock (gate) settings = null; return ProviderStatus(); }
        var validated = OpenRouterJevProvider.Validate(input);
        lock (gate) settings = validated;
        return ProviderStatus();
    }

    public DecisionPolicies GetPolicies() { lock (gate) return policies.Clone(); }

    public DecisionPolicies ConfigurePolicies(DecisionPolicies input)
    {
        if (input.Version != 1) throw new ArgumentException("Unsupported policies version.");
        input.Validate();
        lock (gate)
        {
            policies = input.Clone();
            Directory.CreateDirectory(Path.GetDirectoryName(PoliciesPath())!);
            DslJson.SaveAtomic(PoliciesPath(), policies);
        }
        return GetPolicies();
    }

    // ---- performance catalog storage ----

    private string CatalogPath(string projectId)
    {
        studio.Project(projectId); // known project
        return Path.Combine(studio.StorePath("catalog", projectId), "performance-catalog.json");
    }

    private PerformanceCatalog? LoadCatalog(string path) =>
        File.Exists(path) ? DslJson.Load<PerformanceCatalog>(path) : null;

    public PerformanceCatalog ReadCatalog(string projectId) =>
        LoadCatalog(CatalogPath(projectId)) ?? throw new KeyNotFoundException("No performance catalog exists for this project.");

    public object CatalogStatus(string projectId)
    {
        var project = studio.Project(projectId);
        var path = CatalogPath(projectId);
        var catalog = LoadCatalog(path);
        if (catalog == null) return new { present = false };
        var liveHash = project.Manifest != null ? FilmCompiler.Hash(project.Manifest) : null;
        var errors = project.Manifest != null
            ? CatalogTools.Validate(catalog, project.Manifest, liveHash!)
            : new List<string> { "engine not connected; manifest freshness unknown" };
        return new
        {
            present = true,
            hash = FilmCompiler.Hash(catalog),
            manifestHash = catalog.ManifestSha256,
            liveManifestHash = liveHash,
            fresh = liveHash != null && catalog.ManifestSha256 == liveHash && errors.Count == 0,
            errors,
            performances = catalog.Performances?.Count ?? 0,
            evidenceBacked = PerformanceMatchTask.Eligible(catalog).Count,
        };
    }

    public async Task<object> ImportCatalog(string projectId, PerformanceCatalog catalog, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (catalog == null) throw new ArgumentException("A performance catalog is required.");
            // Freshness and binding checks need the live manifest; import stores the
            // file even when validation reports errors, so curators can inspect status.
            await studio.Connect(projectId, ct);
            var path = CatalogPath(projectId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DslJson.SaveAtomic(path, catalog);
            return CatalogStatus(projectId);
        }
        finally { serial.Release(); }
    }

    public async Task<object> ScaffoldCatalog(string projectId, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            var project = await studio.Connect(projectId, ct);
            var path = CatalogPath(projectId);
            if (File.Exists(path))
            {
                // A scaffold is a starting point, never an overwrite: existing
                // imported or curated descriptions and evidence are preserved.
                // Status still reports freshness so a stale manifest claim shows.
                return new
                {
                    scaffolded = false,
                    note = "An existing catalog was preserved untouched. To replace it, import a corrected catalog file explicitly.",
                    status = CatalogStatus(projectId),
                };
            }
            var catalog = new PerformanceCatalog
            {
                ManifestSha256 = FilmCompiler.Hash(project.Manifest),
                Performances = project.Manifest!.Actors.SelectMany(a => a.Clips.Select(c => new PerformanceEntry { Actor = a.Id, Clip = c })).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DslJson.SaveAtomic(path, catalog);
            return new
            {
                scaffolded = true,
                note = "Scaffold pinned to the live manifest. Add evidence-backed meanings before matching; entries without evidence are never offered.",
                status = CatalogStatus(projectId),
            };
        }
        finally { serial.Release(); }
    }

    /// <summary>Bounded decision preparation for generative planning. When the
    /// decision provider is configured, performance matching is enabled and
    /// calibrated, and a usable catalog exists, this asks the performance-match
    /// task with a deterministic requestId derived from the decision inputs
    /// (identical inputs reuse the persisted record — no provider re-ask).
    /// Only a selected entry's evidence enters generation. Unconfigured,
    /// disabled, uncalibrated, stale, empty or no-match cases inject no
    /// semantic claims; generation otherwise proceeds unchanged.</summary>
    public async Task<PerformanceEvidencePreparation> PreparePerformanceEvidence(string projectId, string intent, CancellationToken ct, string? preparationId = null)
    {
        DecisionPolicies snapshotPolicies; DecisionProviderSettings? snapshotSettings;
        lock (gate) { snapshotPolicies = policies; snapshotSettings = settings; }
        var policy = snapshotPolicies.PerformanceMatch;
        if (!policy.Enabled || snapshotSettings == null || policy.MinConfidence == null)
            return PerformanceEvidencePreparation.None; // no automatic selection is possible; nothing is asked or recorded
        var project = studio.Project(projectId);
        if (project.Manifest == null || !File.Exists(CatalogPath(projectId)))
            return PerformanceEvidencePreparation.None;
        var (record, eligible) = await PerformanceMatchCore(projectId, intent, null, ct, preparationId);
        if (record.Outcome != DecisionOutcomes.Selected || record.SelectedCandidateId == null)
            return new PerformanceEvidencePreparation(null, record.RequestId, record.InputHash, record.Outcome, null);
        var entry = eligible.FirstOrDefault(e => PerformanceMatchTask.CandidateId(e) == record.SelectedCandidateId);
        if (entry == null) // catalog shifted between the ask and this read
            return new PerformanceEvidencePreparation(null, record.RequestId, record.InputHash, record.Outcome, null);
        var roots = new string?[] { studio.Root, project.SourceRoot };
        var line = "- " + record.SelectedCandidateId + ": " + PerformanceMatchTask.Describe(entry, roots);
        return new PerformanceEvidencePreparation(line, record.RequestId, record.InputHash, record.Outcome, record.SelectedCandidateId);
    }

    // ---- audit records ----

    private string RecordPath(string projectId, string task, string requestId)
    {
        if (!FilmCompiler.ValidId(requestId)) throw new ArgumentException("A stable requestId is required for safe retries.");
        if (!DecisionTasks.All.Contains(task)) throw new ArgumentException("Unknown decision task.");
        studio.Project(projectId);
        return Path.Combine(studio.StorePath("decisions", projectId, task), requestId + ".json");
    }

    /// <summary>Returns the persisted record when this requestId was decided with
    /// identical inputs; conflicts when inputs changed; null when never asked.</summary>
    private DecisionRecord? Dedupe(string path, string inputHash)
    {
        if (!File.Exists(path)) return null;
        var saved = DslJson.Load<DecisionRecord>(path);
        if (saved.InputHash != inputHash)
            throw new InvalidOperationException("This decision requestId already belongs to different inputs; use a new requestId to re-ask.");
        return saved;
    }

    private static DecisionRecord Persist(string path, DecisionRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DslJson.SaveAtomic(path, record);
        return record;
    }

    public object[] ListRecords(string projectId)
    {
        studio.Project(projectId);
        var root = studio.StorePath("decisions", projectId);
        if (!Directory.Exists(root)) return Array.Empty<object>();
        return Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(DslJson.Load<DecisionRecord>)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => (object)new { r.RequestId, r.Task, r.Outcome, r.Reason, r.SelectedCandidateId, r.Model, r.CreatedAt, candidates = r.CandidateIds.Length })
            .ToArray();
    }

    public DecisionRecord ReadRecord(string projectId, string task, string requestId)
    {
        var path = RecordPath(projectId, task, requestId);
        if (!File.Exists(path)) throw new KeyNotFoundException("Decision record not found.");
        return DslJson.Load<DecisionRecord>(path);
    }

    // ---- shared task plumbing ----

    private sealed class TaskRun
    {
        public required DecisionRecord Record;
        public required DecisionTaskPolicy Policy;
        public DecisionProviderSettings? Settings;
        public string?[] Roots = Array.Empty<string?>();
    }

    /// <summary>Resolves policy/config/identity, computes the input hash and
    /// applies dedupe. Returns null when a saved record already answers this
    /// exact request. A null requestId derives a deterministic id from the
    /// input hash itself ("gen-…"): identical inputs reuse the same record,
    /// changed inputs get a fresh id, so derived asks never conflict and a
    /// repeated generation never re-asks the provider. Short-circuit outcomes
    /// (disabled task, missing provider) are recorded without any provider
    /// call; ProviderId stays "none" until one is actually made.</summary>
    private TaskRun? Begin(string projectId, string task, string? requestId, string questionVersion, object inputs, string?[] roots, out string recordPath, out DecisionRecord? saved)
    {
        var policy = GetPolicies().For(task);
        DecisionProviderSettings? snapshot;
        lock (gate) snapshot = settings;
        var record = new DecisionRecord
        {
            Task = task,
            ProjectId = projectId,
            QuestionVersion = questionVersion,
            ProviderId = "none",
            ProviderEndpoint = snapshot?.Endpoint ?? "",
            Model = snapshot?.Model ?? "",
            MinConfidence = policy.MinConfidence,
        };
        // Hash original inputs, not their redacted transport view. The original
        // values are never persisted by this hash operation. Identity also
        // includes per-task policy (enabled flag
        // and threshold) and provider endpoint+model. Never the API key —
        // rotating a memory-only secret must not orphan audit records.
        var inputHash = FilmCompiler.Hash(new { inputs, endpoint = snapshot?.Endpoint, model = snapshot?.Model, policy.Enabled, policy.MinConfidence, questionVersion });
        record.InputHash = inputHash;
        record.RequestId = requestId ?? "gen-" + inputHash[..32];
        recordPath = RecordPath(projectId, task, record.RequestId);
        saved = Dedupe(recordPath, inputHash);
        if (saved != null) return null;
        return new TaskRun { Record = record, Policy = policy, Settings = snapshot, Roots = roots };
    }

    private async Task<IReadOnlyList<DecisionAnswer>?> Ask(TaskRun run, DecisionRequest request, CancellationToken ct)
    {
        var provider = providerFactory(run.Settings!);
        run.Record.ProviderId = provider.ProviderId;
        try { return await provider.Decide(request, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation does not prove that a remote provider stopped or
            // declined to bill. Preserve that uncertainty so replaying this
            // request ID cannot silently ask (and potentially charge) again.
            run.Record.Outcome = DecisionOutcomes.NeedsReview;
            run.Record.Reason = "Decision request cancelled; remote completion and usage are unknown. No answer was applied. Use a new requestId only if you want another provider call.";
            Persist(RecordPath(run.Record.ProjectId, run.Record.Task, run.Record.RequestId), run.Record);
            throw;
        }
        catch (DecisionProviderException ex)
        {
            run.Record.Outcome = DecisionOutcomes.NeedsReview;
            run.Record.Reason = "provider failure: " + LocalPathScrubber.Scrub(ex.Message, run.Roots);
            return null;
        }
        catch (ArgumentException ex)
        {
            // The assembled request itself violates the protocol bounds (an
            // oversized instruction, a bad candidate set). That is a review
            // outcome with an audit trail, not an unhandled caller error.
            run.Record.Outcome = DecisionOutcomes.NeedsReview;
            run.Record.Reason = "request protocol validation failed: " + LocalPathScrubber.Scrub(ex.Message, run.Roots);
            return null;
        }
    }

    private static void ApplyChoiceOutcome(DecisionRecord record, DecisionAnswer answer, double? minConfidence, IReadOnlyCollection<string> offered)
    {
        record.Answers = new[] { DecisionAnswerRecord.From(answer) };
        if (answer.Choice != DecisionPolicyBounds.NoneCandidateId && (answer.Choice == null || !offered.Contains(answer.Choice)))
        {
            // Defense in depth beyond wire validation: an unknown or malformed
            // selection never applies anything; the original film is preserved.
            record.Outcome = DecisionOutcomes.NeedsReview;
            record.Reason = "The provider returned an unknown or malformed selection; nothing was applied.";
        }
        else if (answer.Choice == DecisionPolicyBounds.NoneCandidateId)
        {
            record.Outcome = DecisionOutcomes.NoMatch;
            record.Reason = "The provider found none of the offered candidates fits the intent.";
        }
        else if (minConfidence == null)
        {
            record.Outcome = DecisionOutcomes.NeedsReview;
            record.Reason = "This task has no calibrated confidence threshold, so nothing was auto-selected; the answer (confidence " +
                (answer.Confidence?.ToString("0.00") ?? "unknown") + ") is recorded for human review. Confidence values only mean something per provider, " +
                "model and task — there is no universal cutoff. Configure an explicit threshold after evaluating real outcomes.";
        }
        else if (answer.Confidence == null || answer.Confidence < minConfidence)
        {
            record.Outcome = DecisionOutcomes.NeedsReview;
            record.Reason = "Confidence " + (answer.Confidence?.ToString("0.00") ?? "unknown") + " is below this task's threshold " + minConfidence.Value.ToString("0.00") + "; review the answers or adjust the policy.";
        }
        else
        {
            record.Outcome = DecisionOutcomes.Selected;
            record.SelectedCandidateId = answer.Choice;
            record.Reason = "Selected with confidence " + answer.Confidence!.Value.ToString("0.00") + ".";
        }
    }

    // ---- tasks ----

    public async Task<DecisionRecord> PerformanceMatch(string projectId, PerformanceMatchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Intent) || request.Intent.Length > DecisionPolicyBounds.MaxIntentChars)
            throw new ArgumentException("An intent (up to " + DecisionPolicyBounds.MaxIntentChars + " characters) is required.");
        return (await PerformanceMatchCore(projectId, request.Intent, request.RequestId, ct)).Record;
    }

    private async Task<(DecisionRecord Record, IReadOnlyList<PerformanceEntry> Eligible)> PerformanceMatchCore(string projectId, string intent, string? requestId, CancellationToken ct, string? preparationId = null)
    {
        await serial.WaitAsync(ct);
        try
        {
            var project = await studio.Connect(projectId, ct);
            var roots = new string?[] { studio.Root, project.SourceRoot };
            var manifestHash = FilmCompiler.Hash(project.Manifest);
            var path = CatalogPath(projectId);
            var catalog = LoadCatalog(path);
            var catalogHash = catalog != null ? FilmCompiler.Hash(catalog) : "";
            var eligible = catalog != null ? PerformanceMatchTask.Eligible(catalog) : Array.Empty<PerformanceEntry>();
            var candidateIds = eligible.Select(PerformanceMatchTask.CandidateId).ToArray();
            var run = Begin(projectId, DecisionTasks.PerformanceMatch, requestId, PerformanceMatchTask.QuestionVersion,
                new { intent, preparationId, manifestHash, catalogHash, candidateIds }, roots, out var recordPath, out var saved);
            if (run == null) return (saved!, eligible);
            var record = run.Record;
            record.ManifestHash = manifestHash;
            record.EvidenceHash = catalogHash;
            record.CandidateIds = candidateIds;
            record.CandidateHashes = eligible.ToDictionary(PerformanceMatchTask.CandidateId, e => FilmCompiler.Hash(e));

            if (!run.Policy.Enabled) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Performance matching is disabled in decision policies."; return (Persist(recordPath, record), eligible); }
            if (catalog == null) { record.Outcome = DecisionOutcomes.NoMatch; record.Reason = "No performance catalog is imported for this project."; return (Persist(recordPath, record), eligible); }
            var errors = CatalogTools.Validate(catalog, project.Manifest!, manifestHash);
            if (errors.Count > 0) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Catalog evidence is not current: " + LocalPathScrubber.Scrub(string.Join("; ", errors.Take(5)), roots); return (Persist(recordPath, record), eligible); }
            if (eligible.Count == 0) { record.Outcome = DecisionOutcomes.NoMatch; record.Reason = "The catalog has no evidence-backed performances to offer."; return (Persist(recordPath, record), eligible); }
            if (eligible.Count > PerformanceMatchTask.MaxCandidates)
            {
                // No silent truncation: nothing is offered when the eligible set
                // exceeds the bounded question. The audit lists the full eligible
                // evidence base and states plainly that nothing was offered.
                record.Outcome = DecisionOutcomes.NeedsReview;
                record.Reason = eligible.Count + " evidence-backed performances exceed the bounded " + PerformanceMatchTask.MaxCandidates +
                    "-candidate question; nothing was offered. Curate the catalog down to the eligible set you mean to compare.";
                return (Persist(recordPath, record), eligible);
            }
            if (run.Settings == null) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Decision provider is not configured."; return (Persist(recordPath, record), eligible); }

            var question = PerformanceMatchTask.BuildQuestion(intent, eligible, roots);
            var state = State(project, intent, roots);
            var answers = await Ask(run, new DecisionRequest(run.Settings.Model, state, new[] { question }), ct);
            if (answers == null) return (Persist(recordPath, record), eligible);
            ApplyChoiceOutcome(record, answers.Single(), run.Policy.MinConfidence, candidateIds);
            return (Persist(recordPath, record), eligible);
        }
        finally { serial.Release(); }
    }

    /// <summary>Manifest-vocabulary camera candidates for one shot, each
    /// compiler-checked in the real shot context. Convenience for the UI;
    /// external agents may submit their own candidate list instead.</summary>
    public async Task<object> SuggestCameras(string projectId, CameraSuggestRequest request, CancellationToken ct)
    {
        if (!FilmCompiler.ValidId(request.ShotId) || request.Film == null) throw new ArgumentException("A shot id and the base film are required.");
        var project = await studio.Connect(projectId, ct);
        var shot = CameraChoiceTask.FindShot(request.Film, request.ShotId);
        return CameraChoiceTask.Suggest(project.Manifest!, shot)
            .Select(c => new { c.Id, camera = c.Camera, description = CameraChoiceTask.Describe(c, project.Manifest!), compiles = CameraChoiceTask.ValidateCandidate(request.Film, request.ShotId, c.Camera, project.Manifest!) == null })
            .ToArray();
    }

    public async Task<DecisionRecord> CameraChoice(string projectId, CameraChoiceRequest request, CancellationToken ct)
    {
        if (!FilmCompiler.ValidId(request.ShotId) || string.IsNullOrWhiteSpace(request.Intent) || request.Intent.Length > DecisionPolicyBounds.MaxIntentChars)
            throw new ArgumentException("A shot id and an intent (up to " + DecisionPolicyBounds.MaxIntentChars + " characters) are required.");
        await serial.WaitAsync(ct);
        try
        {
            FilmPlan film;
            if (request.Film != null) film = request.Film;
            else if (request.DocumentId != null && request.DocumentRevision != null)
                film = studio.Documents.Read(projectId, request.DocumentId, request.DocumentRevision).Film
                    ?? throw new ArgumentException("The referenced document revision has no film.");
            else throw new ArgumentException("Provide the base film or an exact document id + revision.");

            var project = await studio.Connect(projectId, ct);
            var roots = new string?[] { studio.Root, project.SourceRoot };
            var manifest = project.Manifest!;
            var manifestHash = FilmCompiler.Hash(manifest);
            var filmHash = FilmCompiler.Hash(film);
            var shot = CameraChoiceTask.FindShot(film, request.ShotId);

            var supplied = request.Candidates?.Count > 0
                ? request.Candidates.Select(c => new CameraCandidate { Id = c.Id ?? "", Camera = c.Camera ?? new ShotSpec() }).ToList()
                : CameraChoiceTask.Suggest(manifest, shot).ToList();
            if (supplied.Count > CameraChoiceTask.MaxCandidates) throw new ArgumentException("At most " + CameraChoiceTask.MaxCandidates + " camera candidates.");
            if (supplied.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 120) || supplied.Select(c => c.Id).Distinct().Count() != supplied.Count)
                throw new ArgumentException("Camera candidates need unique non-empty ids.");

            // Compiler validation in the real shot context; only compilable,
            // manifest-supported configurations are ever offered to the model.
            var eligible = new List<CameraCandidate>();
            var rejected = new List<string>();
            foreach (var candidate in supplied)
            {
                var error = CameraChoiceTask.ValidateCandidate(film, request.ShotId, candidate.Camera, manifest);
                if (error == null) eligible.Add(candidate); else rejected.Add(candidate.Id + " (" + error + ")");
            }
            var candidateHashes = eligible.ToDictionary(c => c.Id, c => FilmCompiler.Hash(c.Camera));

            var run = Begin(projectId, DecisionTasks.CameraChoice, request.RequestId, CameraChoiceTask.QuestionVersion,
                new { request = request with { RequestId = "" }, filmHash, manifestHash, candidateIds = candidateHashes.Keys.ToArray(), candidateHashes },
                roots, out var recordPath, out var saved);
            if (run == null) return saved!;
            var record = run.Record;
            record.ManifestHash = manifestHash;
            record.FilmHash = filmHash;
            record.DocumentId = request.DocumentId;
            record.DocumentRevision = request.DocumentRevision;
            record.CandidateIds = candidateHashes.Keys.ToArray();
            record.CandidateHashes = candidateHashes;

            if (!run.Policy.Enabled) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Camera choice is disabled in decision policies."; return Persist(recordPath, record); }
            string? baseError;
            try { FilmCompiler.Prepare(film, manifest); baseError = null; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { baseError = ex.Message; }
            if (baseError != null) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "The base film does not compile against the live manifest: " + LocalPathScrubber.Scrub(baseError, roots); return Persist(recordPath, record); }
            if (eligible.Count == 0)
            {
                record.Outcome = DecisionOutcomes.NoMatch;
                record.Reason = "No camera candidate compiles in this shot's context." + (rejected.Count > 0 ? " Rejected: " + LocalPathScrubber.Scrub(string.Join("; ", rejected.Take(5)), roots) : "");
                return Persist(recordPath, record);
            }
            if (run.Settings == null) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Decision provider is not configured."; return Persist(recordPath, record); }

            var question = CameraChoiceTask.BuildQuestion(shot, request.Intent, eligible, manifest, roots);
            var state = State(project, request.Intent, roots);
            state["shot"] = request.ShotId + " (" + shot.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "–" + shot.End.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s): " + LocalPathScrubber.Scrub(shot.Purpose, roots);
            var answers = await Ask(run, new DecisionRequest(run.Settings.Model, state, new[] { question }), ct);
            if (answers == null) return Persist(recordPath, record);
            ApplyChoiceOutcome(record, answers.Single(), run.Policy.MinConfidence, candidateHashes.Keys.ToArray());
            if (record.Outcome == DecisionOutcomes.Selected)
            {
                var chosen = eligible.First(c => c.Id == record.SelectedCandidateId);
                var proposal = CameraChoiceTask.Apply(film, request.ShotId, chosen.Camera);
                // The proposal must compile as a whole before it may be shown;
                // any failure converts to review with the original preserved.
                try { FilmCompiler.Prepare(proposal, manifest); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    record.Outcome = DecisionOutcomes.NeedsReview;
                    record.SelectedCandidateId = null;
                    record.Reason = "The chosen camera failed whole-film validation; the original film is preserved. " + LocalPathScrubber.Scrub(ex.Message, roots);
                    return Persist(recordPath, record);
                }
                record.Proposal = proposal;
                record.Reason += " Proposal changes only shot " + request.ShotId + "'s camera; every other shot, performance, audio cue and subtitle is unchanged. Save remains revision-checked.";
            }
            return Persist(recordPath, record);
        }
        finally { serial.Release(); }
    }

    public async Task<DecisionRecord> AudioMatch(string projectId, AudioMatchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Intent) || request.Intent.Length > DecisionPolicyBounds.MaxIntentChars)
            throw new ArgumentException("An intent (up to " + DecisionPolicyBounds.MaxIntentChars + " characters) is required.");
        if (request.Bus != null && request.Bus is not ("dialogue" or "music" or "sfx")) throw new ArgumentException("Unknown audio bus.");
        await serial.WaitAsync(ct);
        try
        {
            var project = studio.Project(projectId);
            var roots = new string?[] { studio.Root, project.SourceRoot };
            var media = MediaLibrary.List(studio, projectId).ToArray();
            // Only described sounds are candidates; a file name is never treated
            // as evidence of how a sound plays. The audit lists exactly the
            // offered set — never the whole library, never a silent subset.
            var candidates = AudioMatchTask.Eligible(media.Select(m => new AudioCandidate(m.Id, m.Name, m.Description, m.Duration))).ToArray();
            var evidenceHash = FilmCompiler.Hash(candidates.Select(c => new { c.MediaId, c.Name, c.Description, c.DurationSeconds }).OrderBy(c => c.MediaId, StringComparer.Ordinal).ToArray());
            var run = Begin(projectId, DecisionTasks.AudioMatch, request.RequestId, AudioMatchTask.QuestionVersion,
                new { request = request with { RequestId = "" }, evidenceHash, candidateIds = candidates.Select(c => c.MediaId).ToArray() },
                roots, out var recordPath, out var saved);
            if (run == null) return saved!;
            var record = run.Record;
            record.ManifestHash = project.Manifest != null ? FilmCompiler.Hash(project.Manifest) : "";
            record.EvidenceHash = evidenceHash;
            record.CandidateIds = candidates.Select(c => c.MediaId).ToArray();
            record.CandidateHashes = candidates.ToDictionary(c => c.MediaId, c => FilmCompiler.Hash(c));

            if (!run.Policy.Enabled) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Audio matching is disabled in decision policies."; return Persist(recordPath, record); }
            if (candidates.Length == 0)
            {
                record.Outcome = DecisionOutcomes.NoMatch;
                record.Reason = media.Length == 0
                    ? "No sounds are imported for this project."
                    : "No imported sound has a description, and sounds are never matched on file names alone. Describe sounds in the media library first.";
                return Persist(recordPath, record);
            }
            if (candidates.Length > AudioMatchTask.MaxCandidates)
            {
                record.Outcome = DecisionOutcomes.NeedsReview;
                record.Reason = candidates.Length + " described sounds exceed the bounded " + AudioMatchTask.MaxCandidates +
                    "-candidate question; nothing was offered. Narrow the described set you mean to compare.";
                return Persist(recordPath, record);
            }
            if (run.Settings == null) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Decision provider is not configured."; return Persist(recordPath, record); }

            var question = AudioMatchTask.BuildQuestion(request.Intent, request.Bus, candidates, roots);
            var state = State(project, request.Intent, roots);
            var answers = await Ask(run, new DecisionRequest(run.Settings.Model, state, new[] { question }), ct);
            if (answers == null) return Persist(recordPath, record);
            ApplyChoiceOutcome(record, answers.Single(), run.Policy.MinConfidence, record.CandidateIds);
            return Persist(recordPath, record);
        }
        finally { serial.Release(); }
    }

    public async Task<DecisionRecord> FilmRanking(string projectId, FilmRankingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Brief) || request.Brief.Length > ApiDirectorPolicy.MaxBriefChars)
            throw new ArgumentException("A brief (up to " + ApiDirectorPolicy.MaxBriefChars + " characters) is required.");
        var candidates = (request.Candidates ?? new List<FilmCandidateInput>()).Take(FilmRankingTask.MaxCandidates + 1).ToList();
        if (candidates.Count is < 1 or > FilmRankingTask.MaxCandidates) throw new ArgumentException("Ranking takes 1–" + FilmRankingTask.MaxCandidates + " candidate films.");
        if (candidates.Any(c => c == null || !FilmCompiler.ValidId(c.Id) || c.Film == null) || candidates.Select(c => c.Id).Distinct().Count() != candidates.Count)
            throw new ArgumentException("Candidate films need unique stable ids and a film.");
        await serial.WaitAsync(ct);
        try
        {
            var project = await studio.Connect(projectId, ct);
            var roots = new string?[] { studio.Root, project.SourceRoot };
            var manifest = project.Manifest!;
            var manifestHash = FilmCompiler.Hash(manifest);

            var all = new List<FilmRankingCandidate>();
            var eligible = new List<FilmRankingCandidate>();
            var evidence = new Dictionary<string, string>();
            foreach (var input in candidates)
            {
                var candidate = new FilmRankingCandidate { Id = input.Id, Rationale = input.Rationale, Film = input.Film };
                all.Add(candidate);
                var text = FilmRankingTask.CompileEvidence(candidate, manifest, out var reason);
                if (text != null) { eligible.Add(candidate); evidence[candidate.Id] = text; }
                else evidence[input.Id] = LocalPathScrubber.Scrub(reason, roots);
            }
            var candidateHashes = candidates.ToDictionary(c => c.Id, c => FilmCompiler.Hash(c.Film));
            var run = Begin(projectId, DecisionTasks.FilmRanking, request.RequestId, FilmRankingTask.QuestionVersion,
                new { request = request with { RequestId = "" }, manifestHash, candidateIds = candidates.Select(c => c.Id).ToArray(), candidateHashes },
                roots, out var recordPath, out var saved);
            if (run == null) return saved!;
            var record = run.Record;
            record.ManifestHash = manifestHash;
            record.EvidenceHash = FilmCompiler.Hash(evidence.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
            record.CandidateIds = candidates.Select(c => c.Id).ToArray();
            record.CandidateHashes = candidateHashes;

            if (!run.Policy.Enabled) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Film ranking is disabled in decision policies."; return Persist(recordPath, record); }
            if (eligible.Count == 0)
            {
                record.Outcome = DecisionOutcomes.NoMatch;
                record.Reason = "No candidate film compiles against the live manifest. " + FilmRankingTask.ReviewNote;
                record.Ranking = FilmRankingTask.Rank(Array.Empty<DecisionAnswer>(), all, evidence);
                return Persist(recordPath, record);
            }
            if (run.Settings == null) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Decision provider is not configured."; return Persist(recordPath, record); }

            var questions = eligible.Select(c => FilmRankingTask.BuildQuestion(request.Brief, c, evidence[c.Id], roots)).ToArray();
            var state = State(project, request.Brief, roots);
            var answers = await Ask(run, new DecisionRequest(run.Settings.Model, state, questions), ct);
            if (answers == null) return Persist(recordPath, record);
            record.Answers = answers.Select(DecisionAnswerRecord.From).ToArray();
            record.Ranking = FilmRankingTask.Rank(answers, all, evidence);
            var top = record.Ranking.FirstOrDefault();
            if (run.Policy.MinConfidence == null)
            {
                record.Outcome = DecisionOutcomes.NeedsReview;
                record.Reason = "This task has no calibrated confidence threshold, so no top pick was auto-selected; the ranking is recorded for human review. " + FilmRankingTask.ReviewNote;
            }
            else if (top?.Confidence != null && top.Confidence >= run.Policy.MinConfidence)
            {
                record.Outcome = DecisionOutcomes.Selected;
                record.SelectedCandidateId = top.CandidateId;
                record.Reason = "Top candidate scored " + (top.Score?.ToString("0.00") ?? "?") + " with confidence " + top.Confidence.Value.ToString("0.00") + ". " + FilmRankingTask.ReviewNote;
            }
            else
            {
                record.Outcome = DecisionOutcomes.NeedsReview;
                record.Reason = "Top candidate confidence " + (top?.Confidence?.ToString("0.00") ?? "unknown") + " is below this task's threshold " + run.Policy.MinConfidence.Value.ToString("0.00") + ". " + FilmRankingTask.ReviewNote;
            }
            return Persist(recordPath, record);
        }
        finally { serial.Release(); }
    }

    /// <summary>Optional text-only semantic review: the caller supplies a text
    /// (a shot outline, a subtitle pass, a rationale) and requested checks —
    /// noul statements or ordinal scale ratings. The answers are the result;
    /// nothing is selected, applied or saved. This never judges rendered video
    /// and never replaces compilation or rendering verification.</summary>
    public async Task<DecisionRecord> SemanticReview(string projectId, SemanticReviewRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > SemanticReviewTask.MaxSubjectChars)
            throw new ArgumentException("A review subject text (up to " + SemanticReviewTask.MaxSubjectChars + " characters) is required.");
        if (request.Label != null && request.Label.Length > SemanticReviewTask.MaxLabelChars)
            throw new ArgumentException("A subject label is limited to " + SemanticReviewTask.MaxLabelChars + " characters.");
        var checks = request.Checks ?? new List<SemanticCheckInput>();
        if (checks.Count is < 1 or > SemanticReviewTask.MaxChecks)
            throw new ArgumentException("A semantic review holds 1–" + SemanticReviewTask.MaxChecks + " checks.");
        if (checks.Any(c => c == null || !FilmCompiler.ValidId(c.Id)) || checks.Select(c => c.Id).Distinct().Count() != checks.Count)
            throw new ArgumentException("Checks need unique stable ids.");
        foreach (var c in checks)
        {
            if (c.Kind is not ("noul" or "score")) throw new ArgumentException("Check " + c.Id + " kind must be noul or score.");
            if (string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > SemanticReviewTask.MaxCheckTextChars)
                throw new ArgumentException("Check " + c.Id + " needs question text (up to " + SemanticReviewTask.MaxCheckTextChars + " characters).");
            if (c.Kind == "noul" && c.Legend is { Count: > 0 }) throw new ArgumentException("Noul check " + c.Id + " takes no legend; the criteria are the fixed false/true poles.");
            if (c.Kind == "score" && (c.Legend == null || c.Legend.Count is < SemanticReviewTask.MinLegend or > SemanticReviewTask.MaxLegend ||
                c.Legend.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > SemanticReviewTask.MaxLegendEntryChars)))
                throw new ArgumentException("Score check " + c.Id + " needs an ordered legend of " + SemanticReviewTask.MinLegend + "–" + SemanticReviewTask.MaxLegend +
                    " labels (each up to " + SemanticReviewTask.MaxLegendEntryChars + " characters).");
        }
        await serial.WaitAsync(ct);
        try
        {
            var project = studio.Project(projectId); // text-only: no engine connection required
            var roots = new string?[] { studio.Root, project.SourceRoot };
            var scrub = (string? text) => LocalPathScrubber.Scrub(text, roots);
            var run = Begin(projectId, DecisionTasks.SemanticReview, request.RequestId, SemanticReviewTask.QuestionVersion,
                request with { RequestId = "" }, roots, out var recordPath, out var saved);
            if (run == null) return saved!;
            var record = run.Record;
            record.ManifestHash = project.Manifest != null ? FilmCompiler.Hash(project.Manifest) : "";
            record.EvidenceHash = FilmCompiler.Hash(scrub(request.Subject));

            if (!run.Policy.Enabled) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Semantic review is disabled in decision policies."; return Persist(recordPath, record); }
            if (run.Settings == null) { record.Outcome = DecisionOutcomes.NeedsReview; record.Reason = "Decision provider is not configured."; return Persist(recordPath, record); }

            var questions = checks.Select(c => SemanticReviewTask.BuildCheck(c.Id, c.Kind, c.Question, c.Legend, request.Subject, request.Label, roots)).ToArray();
            var state = new Dictionary<string, string> { ["subject"] = scrub(request.Label) is { Length: > 0 } label ? label : "unlabeled text" };
            var answers = await Ask(run, new DecisionRequest(run.Settings.Model, state, questions), ct);
            if (answers == null) return Persist(recordPath, record);
            record.Answers = answers.Select(DecisionAnswerRecord.From).ToArray();
            record.Outcome = DecisionOutcomes.Completed;
            record.Reason = "Completed " + answers.Count + " requested text check(s); the answers are the review. " + SemanticReviewTask.ReviewNote;
            return Persist(recordPath, record);
        }
        finally { serial.Release(); }
    }

    private static Dictionary<string, string> State(StudioProject project, string intent, string?[] roots)
    {
        var state = new Dictionary<string, string> { ["intent"] = LocalPathScrubber.Scrub(intent, roots) };
        if (!string.IsNullOrWhiteSpace(project.Manifest?.Game)) state["game"] = LocalPathScrubber.Scrub(project.Manifest.Game, roots);
        return state;
    }
}
