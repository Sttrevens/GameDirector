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
    public string RequestHash { get; set; } = "";
    public ProductionRequest Request { get; set; } = new();
    public string Endpoint { get; set; } = "";
    public CapabilityManifest Manifest { get; set; } = new();
    public string State { get; set; } = "Queued";
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
    private readonly object gate = new();
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, StudioProject> projects = new();
    private readonly Dictionary<string, ProductionJob> jobs = new();
    private readonly Dictionary<string, CancellationTokenSource> active = new();
    public Studio(string root)
    {
        Documents = new FilmDocuments(this);
        Root = OutputPolicy.RequireOutput(root);
        // Read and validate protected roots before creating even the ownership file.
        var savedProjects = Directory.Exists(Path.Combine(Root, "projects"))
            ? Directory.GetFiles(Path.Combine(Root, "projects"), "*.json").Select(DslJson.Load<StudioProject>).ToArray() : Array.Empty<StudioProject>();
        foreach (var p in savedProjects) { if (!FilmCompiler.ValidId(p.Id)) throw new IOException("Invalid saved project ID."); if (p.SourceRoot != null) CheckSource(p.SourceRoot); }
        Directory.CreateDirectory(Root);
        ownership = new FileStream(StorePath(".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identityPath = StorePath(".identity.json");
        StoreId = File.Exists(identityPath) ? DslJson.Load<string>(identityPath) : Guid.NewGuid().ToString("N");
        if (!File.Exists(identityPath)) DslJson.SaveAtomic(identityPath, StoreId);
        Directory.CreateDirectory(Path.Combine(Root, "projects")); Directory.CreateDirectory(Path.Combine(Root, "jobs"));
        foreach (var p in savedProjects) projects.Add(p.Id, p);
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "jobs"), "job.json", SearchOption.AllDirectories))
        {
            var job = DslJson.Load<ProductionJob>(file);
            if (!FilmCompiler.ValidId(job.Id)) throw new IOException("Invalid saved job ID.");
            if (job.State is "Queued" or "Running") { job.State = "Interrupted"; job.Progress = "Workbench restarted; resume explicitly to continue."; Save(job); }
            jobs.Add(job.Id, job);
        }
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
    public object Snapshot() { lock (gate) return new { projects = Copy(projects.Values.ToList()), jobs = Copy(jobs.Values.OrderByDescending(j => j.CreatedAt).ToList()) }; }
    public async Task<StudioProject> Connect(string id, CancellationToken ct)
    {
        var p = Project(id);
        using var client = new DirectorBridgeClient(p.Endpoint);
        CapabilityManifest manifest;
        try { manifest = await client.GetManifestAsync(ct); }
        catch (HttpRequestException) { throw new InvalidOperationException("项目已保存，但暂时无法连接引擎（" + p.Endpoint + "）。请检查适配器是否启动、地址是否正确。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("项目已保存，但等待引擎回应超时。请检查引擎状态后重试。"); }
        lock (gate)
        {
            var current = projects[id];
            if (current.Endpoint != p.Endpoint) throw new InvalidOperationException("Project connection changed; refresh again.");
            if (!manifest.Capabilities.TryGetValue(CapabilityKeys.ProjectSourceRoot, out var source)) throw new InvalidOperationException("Update the adapter: it must declare project.sourceRoot before managed production.");
            CheckSource(source);
            if (current.SourceRoot != null && current.SourceRoot != OutputPolicy.Physical(source)) throw new InvalidOperationException("Connected engine differs from the registered source project.");
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
        var request = Copy(input); var requestHash = FilmCompiler.Hash(request);
        lock (gate) { var prior = FindRequest(request, requestHash); if (prior != null) return Copy(prior); }
        var p = await Connect(request.ProjectId, ct);
        var prepared = FilmCompiler.Prepare(request.Film, p.Manifest!);
        MediaLibrary.Validate(this, p.Id, prepared.Plan);
        lock (gate)
        {
            var prior = FindRequest(request, requestHash); if (prior != null) return Copy(prior);
            if (jobs.Values.Count(j => j.State is "Queued" or "Running") >= MaxActiveJobs) throw new InvalidOperationException("Production queue is full.");
            var job = new ProductionJob { Id = Guid.NewGuid().ToString("N"), Request = request, RequestHash = requestHash, Endpoint = p.Endpoint, Manifest = p.Manifest!, TotalShots = prepared.Shots.Count };
            jobs.Add(job.Id, job); Directory.CreateDirectory(JobRoot(job)); Save(job); queue.Writer.TryWrite(job.Id); return Copy(job);
        }
    }
    private ProductionJob? FindRequest(ProductionRequest request, string hash)
    {
        var prior = jobs.Values.FirstOrDefault(j => j.Request.ProjectId == request.ProjectId && j.Request.RequestId == request.RequestId);
        if (prior != null && prior.RequestHash != hash) throw new InvalidOperationException("requestId already belongs to different inputs; use a new revision ID.");
        return prior;
    }
    public ProductionJob Job(string id) { lock (gate) return Copy(jobs.TryGetValue(id, out var j) ? j : throw new KeyNotFoundException("Job not found.")); }
    public ProductionJob Cancel(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var j)) throw new KeyNotFoundException("Job not found.");
            if (active.TryGetValue(id, out var cts)) cts.Cancel();
            else if (j.State == "Queued") { j.State = "Cancelled"; j.Progress = "Cancelled before capture."; Save(j); }
            return Copy(j);
        }
    }
    public ProductionJob Resume(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var j)) throw new KeyNotFoundException("Job not found.");
            if (j.State is "Running" or "Queued" or "Verified") return Copy(j);
            j.State = "Queued"; j.Progress = "Resume queued; original inputs retained."; Save(j); queue.Writer.TryWrite(id); return Copy(j);
        }
    }
    public void CheckSource(string source)
    {
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source)) throw new ArgumentException("Source project root must be an existing absolute directory.");
        var physical = OutputPolicy.Physical(source);
        if (OutputPolicy.Contains(physical, Root) || OutputPolicy.Contains(Root, physical))
            throw new IOException("Game source and Workbench storage must be separate directories.");
    }
    public string StorePath(params string[] parts)
    {
        var path = OutputPolicy.RequireOutput(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
        if (!OutputPolicy.Contains(Root, path)) throw new IOException("Storage path escaped the Workbench store.");
        lock (gate) foreach (var project in projects.Values) if (project.SourceRoot != null) OutputPolicy.RequireOutput(path, project.SourceRoot);
        return path;
    }
    public object StorageStatus() { lock (gate) return new { root = Root, policy = "external-production-storage", sourceProjects = projects.Values.Select(p => new { p.Id, p.SourceRoot }).ToArray() }; }
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
                job = jobs[id]; if (job.State != "Queued") continue;
                cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); active[id] = cancel;
                job.State = "Running"; job.CompletedShots = 0; job.ReusedShots = 0; Save(job);
            }
            try { await Produce(job, cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Update(job, () => { job.State = stoppingToken.IsCancellationRequested ? "Interrupted" : "Cancelled"; job.Progress = "Stopped; completed frames and shots retained."; }); }
            catch (Exception ex) { Update(job, () => { job.State = "Failed"; job.Progress = ex.Message; }); }
            finally { lock (gate) { active.Remove(id); cancel.Dispose(); } }
        }
    }
    private async Task Produce(ProductionJob job, CancellationToken ct)
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
            var cache = StorePath("cache", job.Request.ProjectId, shot.CacheKey);
            var existing = File.Exists(Path.Combine(cache, "job.json"));
            var reused = false;
            Update(job, () => job.Progress = shot.Id + (existing ? " · checking saved take" : " · capturing"));
            string video;
            if (existing)
            {
                // The immutable cache key includes live source identity; Resume verifies receipts and pixels.
                var cached = TakeJobs.Load(cache);
                reused = cached.State == "Verified";
                if (cached.ManifestSha256 != film.ManifestHash) throw new InvalidOperationException("Cached take source identity mismatch.");
                video = await TakeRecorder.Resume(cache, p => Update(job, () => job.Progress = p), ct);
            }
            else
            {
                var timeline = Path.Combine(JobRoot(job), shot.Id + ".json"); DslJson.SaveAtomic(timeline, shot.Timeline);
                // A failed start before the job checkpoint can leave a directory. Preserve it as an orphan.
                if (Directory.Exists(cache)) Directory.Move(cache, cache + ".orphan-" + Guid.NewGuid().ToString("N"));
                video = await TakeRecorder.Record(timeline, cache, job.Endpoint, film.Plan.FrameRate, film.Plan.Width, film.Plan.Height,
                    p => Update(job, () => job.Progress = p), ct, film.ManifestHash);
            }
            edit.Sources[shot.Id] = video;
            edit.Ranges.Add(new EditRange { Source = shot.Id, Start = shot.Start, End = shot.End, Reason = shot.Purpose, Beat = shot.Id });
            Update(job, () => { job.CompletedShots++; if (reused) job.ReusedShots++; });
        }
        Update(job, () => job.Progress = "Assembling and verifying the film");
        var editPath = Path.Combine(JobRoot(job), "edit.json"); DslJson.SaveAtomic(editPath, edit);
        var final = await EditRenderer.Render(editPath, Path.Combine(JobRoot(job), "delivery-" + Guid.NewGuid().ToString("N")), ct);
        var hash = MediaTools.Hash(final);
        Update(job, () => { job.Video = final; job.VideoHash = hash; job.State = "Verified"; job.Progress = "Picture, supplied sound and duration verified; creative review still required."; });
    }
    public override void Dispose() { base.Dispose(); ownership.Dispose(); }
}
