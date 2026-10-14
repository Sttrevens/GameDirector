using System.Text.Json.Nodes;
using System.Threading.Channels;
using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Workbench;

public sealed class StudioProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "";
    public string? SourceRoot { get; set; }
    public string Endpoint { get; set; } = BridgeDefaults.UnityEndpoint;
    public JsonNode? Inventory { get; set; }
    public CapabilityManifest? Manifest { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
}
public sealed class ProductionRequest
{
    public string RequestId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public FilmPlan Film { get; set; } = new();
    public string? DocumentId { get; set; }
    public long? DocumentRevision { get; set; }
}
public sealed class ProductionJob
{
    public string Id { get; set; } = "";
    public ProductionRequest Request { get; set; } = new();
    public string Endpoint { get; set; } = "";
    public CapabilityManifest Manifest { get; set; } = new();
    public ProductionJobState State { get; set; } = ProductionJobState.Queued;
    public string Progress { get; set; } = "";
    public int CompletedShots { get; set; }
    public int ReusedShots { get; set; }
    public int TotalShots { get; set; }
    public string? Video { get; set; }
    public string? VideoHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// One process owns the store; one serial worker owns all engine mutations.
// HTTP clients only submit immutable snapshots and inspect persisted receipts.
public sealed class Studio : BackgroundService
{
    /// <summary>Backpressure bound: queued+running jobs admitted at once.</summary>
    public const int MaxActiveJobs = 16;
    public string Root { get; }
    public string StoreId { get; }
    public FilmDocuments Documents { get; }
    private readonly FileStream ownership;
    private readonly ILogger<Studio> logger;
    private readonly object gate = new();
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, StudioProject> projects = new();
    private readonly Dictionary<string, ProductionJob> jobs = new();
    private readonly Dictionary<string, CancellationTokenSource> active = new();
    public Studio(string root, ILogger<Studio>? logger = null)
    {
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Studio>.Instance;
        Documents = new FilmDocuments(this);
        Root = OutputPolicy.RequireOutput(root);
        // The store schema check is read-only: refusal comes first, and a too-new
        // or invalid store is never written. The single stamp write happens after
        // .owner is held and the load succeeded (below).
        var priorSchema = StoreSchema.Check(Root);
        // Read and validate protected roots before creating even the ownership file.
        var savedProjects = Directory.Exists(Path.Combine(Root, "projects"))
            ? Directory.GetFiles(Path.Combine(Root, "projects"), "*.json").Select(StoredJson.Load<StudioProject>).ToArray() : Array.Empty<StudioProject>();
        foreach (var p in savedProjects) { if (!FilmCompiler.ValidId(p.Id)) throw new StoreIntegrityFault("Invalid saved project ID."); if (p.SourceRoot != null) CheckSource(p.SourceRoot); }
        Directory.CreateDirectory(Root);
        ownership = new FileStream(StorePath(".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identityPath = StorePath(".identity.json");
        StoreId = File.Exists(identityPath) ? StoredJson.Load<string>(identityPath) : Guid.NewGuid().ToString("N");
        if (!File.Exists(identityPath)) DslJson.SaveAtomic(identityPath, StoreId);
        Directory.CreateDirectory(Path.Combine(Root, "projects")); Directory.CreateDirectory(Path.Combine(Root, "jobs"));
        foreach (var p in savedProjects) projects.Add(p.Id, p);
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "jobs"), "job.json", SearchOption.AllDirectories))
        {
            var job = StoredJson.Load<ProductionJob>(file);
            if (!FilmCompiler.ValidId(job.Id)) throw new StoreIntegrityFault("Invalid saved job ID.");
            if (job.State is ProductionJobState.Queued or ProductionJobState.Running) { ProductionJobFlow.Move(job, ProductionJobState.Interrupted); job.Progress = "Workbench restarted; resume explicitly to continue."; Save(job); }
            jobs.Add(job.Id, job);
        }
        // Ownership held and the load succeeded: now the store may be stamped.
        StoreSchema.Stamp(Root, priorSchema);
    }
    public static string LocalEndpoint(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" ||
            !System.Net.IPAddress.TryParse(uri.Host, out var ip) || !System.Net.IPAddress.IsLoopback(ip))
            throw new ArgumentException("Use a loopback bridge URL such as " + BridgeDefaults.UnityEndpoint + ".");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private static T Copy<T>(T value) => DslJson.Deserialize<T>(DslJson.Serialize(value));
    public StudioProject Register(StudioProject input)
    {
        if (!FilmCompiler.ValidId(input.Id) || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200) throw new ArgumentException("Project needs a stable ID and a name.");
        input.Endpoint = LocalEndpoint(input.Endpoint);
        if (!string.IsNullOrWhiteSpace(input.SourceRoot)) CheckSource(input.SourceRoot);
        lock (gate)
        {
            var p = new StudioProject { Id = input.Id, Name = input.Name, Engine = input.Engine, SourceRoot = input.SourceRoot == null ? null : OutputPolicy.Physical(input.SourceRoot), Endpoint = input.Endpoint, Inventory = input.Inventory?.DeepClone() };
            if (projects.TryGetValue(p.Id, out var old))
            {
                p.SourceRoot ??= old.SourceRoot;
                if (p.SourceRoot == old.SourceRoot && p.Engine == old.Engine) {
                    p.Inventory ??= old.Inventory;
                    if (p.Endpoint == old.Endpoint) { p.Manifest = old.Manifest; p.ConnectedAt = old.ConnectedAt; }
                }
            }
            projects[p.Id] = p; SaveProject(p); return Copy(p);
        }
    }
    public StudioProject Project(string id) { lock (gate) return Copy(projects.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException("Project not found.")); }
    public object Snapshot() { lock (gate) return new { projects = Copy(projects.Values.ToList()), jobs = jobs.Values.OrderByDescending(j => j.CreatedAt).Select(j => ProductionJobFlow.View(Copy(j))).ToArray() }; }
    public async Task<StudioProject> Connect(string id, CancellationToken ct)
    {
        var p = Project(id);
        using var client = new DirectorBridgeClient(p.Endpoint);
        CapabilityManifest manifest;
        try { manifest = await client.GetManifestAsync(ct); }
        catch (BridgeException ex) { throw Faults.Bridge(ex, "Project saved, but the engine request did not complete. Check the adapter and request before retrying: "); }
        lock (gate)
        {
            var current = projects[id];
            if (current.Endpoint != p.Endpoint) throw new StateConflictFault("Project connection changed; refresh again.", retryable: true);
            if (!manifest.Capabilities.TryGetValue(CapabilityKeys.ProjectSourceRoot, out var source)) throw new EngineUnavailableFault("Update the adapter: it must declare project.sourceRoot before managed production.");
            CheckSource(source);
            if (current.SourceRoot != null && current.SourceRoot != OutputPolicy.Physical(source)) throw new StateConflictFault("Connected engine differs from the registered source project.", retryable: false);
            current.SourceRoot = OutputPolicy.Physical(source);
            current.Manifest = manifest; current.ConnectedAt = DateTimeOffset.UtcNow; SaveProject(current); return Copy(current);
        }
    }
    public async Task<PreparedFilm> Validate(ProductionRequest request, CancellationToken ct)
    {
        var p = await Connect(request.ProjectId, ct);
        var prepared = FilmCompiler.Prepare(request.Film, p.Manifest!);
        MediaLibrary.Validate(this, p.Id, prepared.Plan);
        return prepared;
    }
    public async Task<ProductionJob> Submit(ProductionRequest input, CancellationToken ct)
    {
        if (!FilmCompiler.ValidId(input.RequestId)) throw new ArgumentException("A stable requestId is required for safe retries.");
        var request = Copy(input);
        lock (gate) { var prior = FindRequest(request); if (prior != null) return Copy(prior); }
        var p = await Connect(request.ProjectId, ct);
        var prepared = FilmCompiler.Prepare(request.Film, p.Manifest!);
        MediaLibrary.Validate(this, p.Id, prepared.Plan);
        lock (gate)
        {
            var prior = FindRequest(request); if (prior != null) return Copy(prior);
            RequireQueueSpace();
            var job = new ProductionJob { Id = Guid.NewGuid().ToString("N"), Request = request, Endpoint = p.Endpoint, Manifest = p.Manifest!, TotalShots = prepared.Shots.Count };
            jobs.Add(job.Id, job); Directory.CreateDirectory(JobRoot(job)); Save(job); queue.Writer.TryWrite(job.Id); return Copy(job);
        }
    }
    private void RequireQueueSpace()
    {
        if (jobs.Values.Count(j => j.State is ProductionJobState.Queued or ProductionJobState.Running) >= MaxActiveJobs) throw new QueueFullFault("Production queue is full.");
    }
    private ProductionJob? FindRequest(ProductionRequest request)
    {
        var prior = jobs.Values.FirstOrDefault(j => j.Request.ProjectId == request.ProjectId && j.Request.RequestId == request.RequestId);
        // The original inputs are stored, so both sides are recomputed with the
        // current identity function instead of trusting a stored hash.
        if (prior != null && ContentIdentity.Of(prior.Request) != ContentIdentity.Of(request)) throw new RequestConflictFault("requestId already belongs to different inputs; use a new revision ID.");
        return prior;
    }
    public ProductionJob Job(string id) { lock (gate) return Copy(jobs.TryGetValue(id, out var j) ? j : throw new KeyNotFoundException("Job not found.")); }
    public ProductionJob Cancel(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var j)) throw new KeyNotFoundException("Job not found.");
            // Repeated/stale user actions stay idempotent; permission comes from
            // the same table that drives the DTO, not a second state switch.
            if (!ProductionJobFlow.Allows(j.State, "cancel")) return Copy(j);
            if (active.TryGetValue(id, out var cts)) cts.Cancel();
            else { ProductionJobFlow.Move(j, ProductionJobState.Cancelled); j.Progress = "Cancelled before capture."; Save(j); }
            return Copy(j);
        }
    }
    public ProductionJob Resume(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var j)) throw new KeyNotFoundException("Job not found.");
            if (!ProductionJobFlow.Allows(j.State, "resume")) return Copy(j);
            RequireQueueSpace();
            ProductionJobFlow.Move(j, ProductionJobState.Queued); j.Progress = "Resume queued; original inputs retained."; Save(j); queue.Writer.TryWrite(id); return Copy(j);
        }
    }
    public void CheckSource(string source)
    {
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source)) throw new ArgumentException("Source project root must be an existing absolute directory.");
        var physical = OutputPolicy.Physical(source);
        if (OutputPolicy.Contains(physical, Root) || OutputPolicy.Contains(Root, physical))
            throw new ArgumentException("Game source and Workbench storage must be separate directories.");
    }
    public string StorePath(params string[] parts)
    {
        var path = OutputPolicy.RequireOutput(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
        if (!OutputPolicy.Contains(Root, path)) throw new StoreIntegrityFault("Storage path escaped the Workbench store.");
        lock (gate) foreach (var project in projects.Values) if (project.SourceRoot != null) OutputPolicy.RequireOutput(path, project.SourceRoot);
        return path;
    }
    public object StorageStatus()
    {
        // Usage is measured on request but cached briefly: a status call must
        // stay cheap even over a large store. The walk happens outside the gate
        // so measuring never blocks job progress; prune invalidates the cache.
        lock (gate) if (storageUsage is { } fresh && DateTimeOffset.UtcNow - fresh.At < StorageUsageCache) return StorageStatusBody(fresh.Areas);
        var areas = MeasureStorage();
        lock (gate) { storageUsage = (DateTimeOffset.UtcNow, areas); return StorageStatusBody(areas); }
    }
    private (DateTimeOffset At, Dictionary<string, long> Areas)? storageUsage;
    private static readonly TimeSpan StorageUsageCache = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> StorageAreas = new(StringComparer.Ordinal) { "cache", "jobs", "documents", "media" };
    private object StorageStatusBody(Dictionary<string, long> areas) { lock (gate) return new { root = Root, policy = "external-production-storage",
        usage = new { cache = areas["cache"], jobs = areas["jobs"], documents = areas["documents"], media = areas["media"], other = areas["other"], reclaimable = areas["reclaimable"] },
        sourceProjects = projects.Values.Select(p => new { p.Id, p.SourceRoot }).ToArray() }; }
    /// <summary>Cache namespace of the current pipeline: cache/format.N/<project>/<key>.
    /// "format.N" can never collide with a project id (ValidId forbids '.'), so a
    /// direct child of cache/ is unambiguously either a generation or the legacy
    /// pre-namespace layout — and anything a format change makes unreachable is
    /// identifiable as dead by its location alone.</summary>
    private static readonly string CurrentCacheGeneration = "format." + FilmCompiler.CacheFormat;
    private static bool IsOldCacheGeneration(string name) =>
        name.StartsWith("format.", StringComparison.Ordinal) ? name != CurrentCacheGeneration : FilmCompiler.ValidId(name);
    private Dictionary<string, long> MeasureStorage()
    {
        // Links are never followed: usage is neither counted twice nor measured
        // outside the store (same convention as OutputPolicy).
        var areas = new Dictionary<string, long> { ["cache"] = 0, ["jobs"] = 0, ["documents"] = 0, ["media"] = 0, ["other"] = 0, ["reclaimable"] = 0 };
        foreach (var entry in Directory.EnumerateFileSystemEntries(Root))
        {
            if (Directory.Exists(entry))
            {
                var info = new DirectoryInfo(entry); if (info.LinkTarget != null) continue;
                areas[StorageAreas.Contains(info.Name) ? info.Name : "other"] += Measure(entry);
                // Reclaimable = whole cache generations that can never be reached
                // again (old format.N and the legacy cache/<project>/<key> layout).
                if (info.Name == "cache")
                    foreach (var child in Directory.EnumerateDirectories(entry))
                        if (new DirectoryInfo(child).LinkTarget == null && IsOldCacheGeneration(Path.GetFileName(child)))
                            areas["reclaimable"] += Measure(child);
            }
            else try { var info = new FileInfo(entry); if (info.LinkTarget == null) areas["other"] += info.Length; } catch (IOException) { }
        }
        return areas;
    }
    private static long Measure(string path)
    {
        long total = 0; var pending = new Stack<string>(); pending.Push(path);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(dir); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var file in entries)
                try { var info = new FileInfo(file); if (info.LinkTarget == null) total += info.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { entries = Directory.EnumerateDirectories(dir); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var child in entries) if (new DirectoryInfo(child).LinkTarget == null) pending.Push(child);
        }
        return total;
    }
    /// <summary>Explicit, idempotent reclamation of clearly unreferenced
    /// leftovers: .orphan-*/.stale-* cache entries, previous-*/encode-* video
    /// candidates inside take dirs, and frames/ + attempts/ under Verified takes
    /// that do not retain frames (and inside terminal-state job dirs). Documents,
    /// projects, the media library and interrupted work are never touched, and
    /// pruning while a job is queued or running is refused outright: nothing a
    /// job can still reference is ever removed.
    /// The gate is held for the whole prune: no job leaves Queued mid-delete,
    /// and Submit/Resume block until the store is consistent again.</summary>
    public object PruneStorage()
    {
        var removed = new List<string>(); long freed = 0;
        lock (gate)
        {
            if (jobs.Values.Any(j => j.State is ProductionJobState.Queued or ProductionJobState.Running))
                throw new StateConflictFault("Storage is not pruned while a production is queued or running; cancel it or wait for it to finish.", retryable: true);
            void Remove(string path)
            {
                // Links are skipped, never followed or deleted through.
                if (Directory.Exists(path)) { if (new DirectoryInfo(path).LinkTarget != null) return; freed += Measure(path); Directory.Delete(path, true); }
                else if (File.Exists(path)) { var info = new FileInfo(path); if (info.LinkTarget != null) return; freed += info.Length; File.Delete(path); }
                else return;
                removed.Add(Path.GetRelativePath(Root, path));
            }
            void PruneTakeFiles(string takeDir)
            {
                // Failed encode candidates and superseded pictures: provenance
                // for a finished encode that nothing references anymore.
                foreach (var pattern in new[] { "previous-*.mp4", "encode-*.mp4" })
                    foreach (var file in Directory.EnumerateFiles(takeDir, pattern)) Remove(file);
            }
            void PruneTake(string takeDir)
            {
                PruneTakeFiles(takeDir);
                var attempts = Path.Combine(takeDir, "attempts");
                if (TryTakeRetention(takeDir, out var retainFrames) && !retainFrames)
                {
                    // Verified take that chose not to keep frames: picture.mp4,
                    // take.json, job.json and the frames.jsonl provenance stay.
                    if (Directory.Exists(attempts)) Remove(attempts);
                    var frames = Path.Combine(takeDir, "frames"); if (Directory.Exists(frames)) Remove(frames);
                }
                else if (Directory.Exists(attempts))
                    foreach (var child in Directory.EnumerateDirectories(attempts)) PruneTake(child);
            }
            var cache = Path.Combine(Root, "cache");
            if (Directory.Exists(cache))
                foreach (var child in Directory.EnumerateDirectories(cache))
                {
                    if (new DirectoryInfo(child).LinkTarget != null) continue;
                    var generation = Path.GetFileName(child);
                    // Whole generations that can never be reached again: old
                    // format.N namespaces and the legacy cache/<project>/<key> layout.
                    if (IsOldCacheGeneration(generation)) { Remove(child); continue; }
                    foreach (var project in Directory.EnumerateDirectories(child))
                    {
                        if (new DirectoryInfo(project).LinkTarget != null) continue;
                        foreach (var entry in Directory.EnumerateDirectories(project))
                        {
                            if (new DirectoryInfo(entry).LinkTarget != null) continue;
                            var name = Path.GetFileName(entry);
                            if (name.Contains(".orphan-") || name.Contains(".stale-")) Remove(entry);
                            else PruneTake(entry);
                        }
                    }
                }
            var jobsRoot = Path.Combine(Root, "jobs");
            if (Directory.Exists(jobsRoot))
                foreach (var dir in Directory.EnumerateDirectories(jobsRoot))
                {
                    // Only jobs in a terminal state; unknown dirs are never touched.
                    if (!jobs.TryGetValue(Path.GetFileName(dir), out var job) || job.State is not (ProductionJobState.Verified or ProductionJobState.Failed or ProductionJobState.Cancelled)) continue;
                    PruneTakeFiles(dir);
                    var attempts = Path.Combine(dir, "attempts"); if (Directory.Exists(attempts)) Remove(attempts);
                    var frames = Path.Combine(dir, "frames"); if (Directory.Exists(frames)) Remove(frames);
                }
            storageUsage = null;
        }
        return new { removed, bytesFreed = freed };
    }
    /// <summary>Retention read for prune: true only for a readable Verified take
    /// journal. Anything unreadable is left alone (it is not clearly unreferenced).
    /// RetainFrames defaults to false for journals written before the field.
    /// </summary>
    private static bool TryTakeRetention(string takeDir, out bool retainFrames)
    {
        retainFrames = false;
        try
        {
            var job = DslJson.Load<TakeJob>(Path.Combine(takeDir, "job.json"));
            retainFrames = job.RetainFrames;
            return job.State == TakeJobStates.Verified;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return false; }
    }
    private string JobRoot(ProductionJob job) => StorePath("jobs", job.Id);
    private void Save(ProductionJob job) => DslJson.SaveAtomic(Path.Combine(JobRoot(job), "job.json"), job);
    private void SaveProject(StudioProject p) => DslJson.SaveAtomic(StorePath("projects", p.Id + ".json"), p);
    private void Update(ProductionJob j, Action change) { lock (gate) { change(); Save(j); } }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            ProductionJob job; CancellationTokenSource cancel;
            lock (gate)
            {
                job = jobs[id]; if (job.State != ProductionJobState.Queued) continue;
                cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); active[id] = cancel;
                ProductionJobFlow.Move(job, ProductionJobState.Running); job.CompletedShots = 0; job.ReusedShots = 0; Save(job);
            }
            try { await Produce(job, cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { FinishStopped(job, stoppingToken.IsCancellationRequested ? ProductionJobState.Interrupted : ProductionJobState.Cancelled, "Stopped; completed frames and shots retained."); }
            catch (Exception ex) { logger.LogError(ex, "Production {JobId} failed: {Message}", job.Id, ex.Message); FinishStopped(job, ProductionJobState.Failed, Faults.Of(ex).Message); }
            finally { lock (gate) { active.Remove(id); cancel.Dispose(); } }
        }
    }
    private void FinishStopped(ProductionJob job, ProductionJobState state, string progress)
    {
        lock (gate)
        {
            // A final journal write may fail AFTER verification. Never demote a
            // terminal Verified result in a catch path or invent a transition.
            if (!ProductionJobFlow.CanMove(job.State, state)) return;
            Update(job, () => { ProductionJobFlow.Move(job, state); job.Progress = progress; });
        }
    }
    private async Task Produce(ProductionJob job, CancellationToken ct)
    {
        try { await ProduceCore(job, ct); }
        catch (BridgeException ex) { throw Faults.Bridge(ex); }
    }
    private async Task ProduceCore(ProductionJob job, CancellationToken ct)
    {
        var film = FilmCompiler.Prepare(job.Request.Film, job.Manifest);
        MediaLibrary.Validate(this, job.Request.ProjectId, film.Plan);
        var edit = new EditPlan { FrameRate = film.Plan.FrameRate, Width = film.Plan.Width, Height = film.Plan.Height };
        foreach (var cue in film.Plan.Audio)
            edit.Audio.Add(new EditAudioCue { Source = MediaLibrary.Resolve(this, job.Request.ProjectId, cue.MediaId), At = cue.At, SourceStart = cue.SourceStart,
                Duration = cue.Duration, Volume = cue.Volume, FadeIn = cue.FadeIn, FadeOut = cue.FadeOut });
        if (film.Plan.Subtitles.Count > 0)
        {
            var subtitles = StorePath("jobs", job.Id, "captions.srt");
            File.WriteAllText(subtitles, SubtitleRenderer.ToSrt(film.Plan.Subtitles)); edit.Subtitles = subtitles;
        }
        foreach (var shot in film.Shots)
        {
            ct.ThrowIfCancellationRequested();
            var cache = StorePath("cache", "format." + FilmCompiler.CacheFormat, job.Request.ProjectId, shot.CacheKey);
            var existing = File.Exists(Path.Combine(cache, "job.json"));
            var reused = false;
            Update(job, () => job.Progress = shot.Id + (existing ? " · checking saved take" : " · capturing"));
            string? video = null;
            if (existing)
            {
                try
                {
                    // The immutable cache key includes live source identity; Resume verifies receipts and pixels.
                    TakeJobs.Load(cache);
                    // Semantic identity derived from the integrity-verified stored bytes.
                    if (TakeJobs.ManifestIdentity(cache) != film.ManifestHash) throw new TakeCacheIncompatibleException("Cached take source identity mismatch.");
                    var resumed = await TakeRecorder.Resume(cache, p => Update(job, () => job.Progress = p), ct);
                    video = resumed.Video; reused = resumed.Reused;
                }
                catch (TakeCacheIncompatibleException ex)
                {
                    // An entry written by another pipeline era is evidence, never
                    // input: set it aside (orphan convention) and record fresh.
                    Directory.Move(cache, cache + ".stale-" + Guid.NewGuid().ToString("N"));
                    Update(job, () => job.Progress = shot.Id + " · incompatible cache set aside (" + ex.Message + "); capturing");
                }
            }
            if (video == null)
            {
                var timeline = Path.Combine(JobRoot(job), shot.Id + ".json"); DslJson.SaveAtomic(timeline, shot.Timeline);
                // A failed start before the job checkpoint can leave a directory. Preserve it as an orphan.
                if (Directory.Exists(cache)) Directory.Move(cache, cache + ".orphan-" + Guid.NewGuid().ToString("N"));
                video = await TakeRecorder.Record(timeline, cache, job.Endpoint, film.Plan.FrameRate, film.Plan.Width, film.Plan.Height,
                    p => Update(job, () => job.Progress = p), ct, film.ManifestHash, captureFromFrame: shot.CaptureFromFrame);
            }
            edit.Sources[shot.Id] = video;
            edit.Ranges.Add(new EditRange { Source = shot.Id, Start = 0, End = shot.End - shot.Start, Reason = shot.Purpose, Beat = shot.Id });
            Update(job, () => { job.CompletedShots++; if (reused) job.ReusedShots++; });
        }
        Update(job, () => job.Progress = "Assembling and verifying the film");
        var editPath = Path.Combine(JobRoot(job), "edit.json"); DslJson.SaveAtomic(editPath, edit);
        string final;
        try { final = await EditRenderer.Render(editPath, Path.Combine(JobRoot(job), "delivery-" + Guid.NewGuid().ToString("N")), ct); }
        catch (Exception ex) when (ex is not (MediaFailureException or StateConflictException or EngineUnavailableException) && (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException))
        { throw new MediaFailureFault("Film assembly or verification failed; check the media tools and server log.", ex); }
        var hash = MediaTools.Hash(final);
        Update(job, () => { job.Video = final; job.VideoHash = hash; ProductionJobFlow.Move(job, ProductionJobState.Verified); job.Progress = "Picture, supplied sound and duration verified; creative review still required."; });
    }
    public override void Dispose() { base.Dispose(); ownership.Dispose(); }
}
