using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;
using GameDirector.Workbench;

// Configuration's command-line provider ignores a trailing valueless switch
// (or consumes the next option as its value). --open is a real boolean flag.
var commandArgs = args.SelectMany((arg, i) => arg == "--open" && (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
    ? new[] { arg, "true" } : new[] { arg }).ToArray();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = commandArgs, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
var port = builder.Configuration.GetValue("port", BridgeDefaults.WorkbenchPort);
// Body ceiling derives from the largest legitimate upload: one base64-encoded
// sound import plus JSON envelope slack. Never an independent literal.
var maxBodyBytes = (long)(FilmLimits.MaxAudioImportBytes * 4L / 3) + 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = maxBodyBytes; });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; });
// Strict binder errors must reach the same failure policy in Production too,
// rather than short-circuiting with an empty 400 before the endpoint runs.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
// The per-user store root is resolved once, in Core, identically under .NET and
// Unity Mono; an explicit --data always wins over the canonical location.
var storeRoot = builder.Configuration["data"] ?? GameDirectorPaths.WorkbenchStore();
var legacyStore = LegacyMacStore(storeRoot);
if (legacyStore != null)
    Console.WriteLine("WARNING: a store created by an older Unity launcher exists at " + legacyStore + " and is NOT used; to keep its projects, stop the Workbench and move that folder to " + storeRoot + " (docs/install.md).");
builder.Services.AddSingleton(s => new Studio(storeRoot, s.GetRequiredService<ILogger<Studio>>()));
builder.Services.AddHostedService(s => s.GetRequiredService<Studio>());
builder.Services.AddSingleton<MediaInstallService>();
builder.Services.AddHostedService(s => s.GetRequiredService<MediaInstallService>());
// Paid work (model generation, provider decisions) is bounded by server shutdown
// plus per-stage timeouts, never by RequestAborted: a client that times out must
// not strand a paid call without its persisted receipt.
builder.Services.AddSingleton(s => new ApiDirector(applicationStopping: s.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
builder.Services.AddSingleton(s => new DecisionService(s.GetRequiredService<Studio>(), applicationStopping: s.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
var app = builder.Build();
var requestLog = app.Logger;
var instanceId = Guid.NewGuid().ToString("N");
var auth = new WorkbenchAuth();
var authority = "127.0.0.1:" + port;
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.Single(); authority = new Uri(address).Authority;
    var studio = app.Services.GetRequiredService<Studio>();
    WorkbenchAuth.WriteDescriptor(studio.StorePath(GameDirectorPaths.ServiceDescriptorFile), new { product = WorkbenchService.Product, protocolVersion = WorkbenchService.ProtocolVersion, instanceId, studio.StoreId, address, processId = Environment.ProcessId, token = auth.Token });
    if (builder.Configuration.GetValue("open", false)) _ = OpenBrowser(address, auth.Token);
});
app.Use(async (context, next) =>
{
    // Bind, Host, Origin and Fetch Metadata checks prevent remote websites from
    // reading local assets, configuring a provider or starting expensive jobs.
    var expected = authority;
    var origin = context.Request.Headers.Origin.ToString();
    if (context.Request.Host.Value != expected || (origin != "" && origin != "http://" + expected) || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!auth.Allows(context.Request)) {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(Faults.Body(Faults.Of(new UnauthorizedFault()))); return;
        }
        if (context.Request.Method != "GET" && !context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
    }
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        var fault = Faults.Of(ex);
        if (fault.Status >= 500) requestLog.LogError(ex, "Workbench failure ({Code}): {Message}", fault.Code, ex.Message);
        context.Response.StatusCode = fault.Status;
        await context.Response.WriteAsJsonAsync(Faults.Body(fault));
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
// The health payload keeps the minimal-API camelCase naming even though it is
// built as a node: the legacy-store field is conditional and cannot be restated
// as one anonymous-type literal.
var HealthJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
app.MapGet("/api/health", (Studio s) =>
{
    var health = JsonSerializer.SerializeToNode(new { product = WorkbenchService.Product, protocolVersion = WorkbenchService.ProtocolVersion, instanceId, s.StoreId, features = new[] { "film-documents-v1", "production-readiness", "jev-decisions-v1" },
        engines = EngineProfiles.All,
        defaults = new { frameRate = FilmDefaults.FrameRate, width = FilmDefaults.Width, height = FilmDefaults.Height },
        limits = new { scenes = FilmLimits.MaxScenes, shots = FilmLimits.MaxShots, audioCues = FilmLimits.MaxAudioCues, subtitles = FilmLimits.MaxSubtitles,
            maxAudioBytes = MediaFormats.MaxImportBytes, maxScriptBytes = FilmLimits.MaxScriptBytes, maxAudioFormats = MediaFormats.AudioExtensions, outputSeconds = FilmLimits.MaxOutputSeconds } },
        HealthJson)!.AsObject();
    if (legacyStore != null) health["legacyStore"] = legacyStore;
    return health;
});
app.MapPost("/api/session/code", () => new { code = auth.MintCode() });
app.MapPost("/api/session", (SessionExchange input, HttpContext context) =>
{
    var session = auth.Exchange(input.Code);
    context.Response.Cookies.Append(WorkbenchAuth.CookieName, session, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
    return new { authenticated = true };
});
app.MapGet("/api/doctor", (Studio s, CancellationToken ct) => DirectorProduct.Doctor(null, ct));
// Media bootstrap: explicit local user action only; options are platform-filtered.
app.MapMediaInstall();
app.MapPost("/api/folders", (FolderRequest request) => ProjectFolders.Browse(request));
app.MapGet("/api/projects/{id}/media", (string id, Studio s) => MediaLibrary.List(s, id));
app.MapPost("/api/projects/{id}/media", (string id, MediaImport input, Studio s, IHostApplicationLifetime lifetime) => MediaLibrary.Import(s, id, input, lifetime.ApplicationStopping));
app.MapPost("/api/projects/{id}/media/{mediaId}", (string id, string mediaId, MediaDescription input, Studio s) => MediaLibrary.Describe(s, id, mediaId, input.Description));
app.MapGet("/api/storage", (Studio s) => s.StorageStatus());
// Explicit user action only; refuses while any production is queued or running.
app.MapPost("/api/storage/prune", (Studio s) => s.PruneStorage());
app.MapGet("/api/state", (Studio s) => s.Snapshot());
app.MapPost("/api/projects", (StudioProject p, Studio s) => s.Register(p));
app.MapPost("/api/projects/{id}/connect", (string id, Studio s, CancellationToken ct) => s.Connect(id, ct));
app.MapGet("/api/projects/{id}/starter", (string id, Studio s) => FilmCompiler.Starter(s.Project(id).Manifest ?? throw new EngineUnavailableFault("Connect the engine first.")));
// Legacy reads remain useful; unconditional legacy writes cannot bypass revisions.
app.MapGet("/api/projects/{id}/draft", (string id, Studio s) => Results.Json(s.Documents.Read(id, "default").Film));
app.MapPost("/api/projects/{id}/draft", (string id, Studio s) => { s.Project(id); throw new RevisionRequired(); });
app.MapGet("/api/projects/{id}/documents", (string id, Studio s) => s.Documents.List(id));
app.MapGet("/api/projects/{id}/documents/{documentId}", (string id, string documentId, long? revision, Studio s) => s.Documents.Read(id, documentId, revision));
app.MapPost("/api/projects/{id}/documents/{documentId}", (string id, string documentId, SaveFilmDocument input, Studio s) => s.Documents.Save(id, documentId, input));
app.MapPost("/api/projects/{id}/documents/{documentId}/readiness", (string id, string documentId, CheckFilmDocument input, Studio s, CancellationToken ct) =>
    ProductionReadiness.Check(s, id, FilmDocuments.ProductionFilm(s.Documents.Read(id, documentId, input.Revision), input.Preview), ct));
app.MapPost("/api/projects/{id}/documents/{documentId}/produce", async (string id, string documentId, ProduceFilmDocument input, Studio s, CancellationToken ct) =>
    ProductionJobFlow.View(await s.Submit(new ProductionRequest { ProjectId = id, RequestId = input.RequestId, DocumentId = documentId, DocumentRevision = input.Revision,
        Film = FilmDocuments.ProductionFilm(s.Documents.Read(id, documentId, input.Revision), input.Preview) }, ct)));
app.MapPost("/api/validate", async (ProductionRequest r, Studio s, CancellationToken ct) => { var f = await s.Validate(r, ct); return new { valid = true, manifestHash = f.ManifestHash, shots = f.Shots.Select(x => new { x.Id, x.Purpose, x.Start, x.End, x.CacheKey }), duration = f.Shots.Sum(x => x.End - x.Start) }; });
app.MapPost("/api/jobs", async (ProductionRequest r, Studio s, CancellationToken ct) => ProductionJobFlow.View(await s.Submit(r, ct)));
app.MapGet("/api/jobs/{id}", (string id, Studio s) => ProductionJobFlow.View(s.Job(id)));
app.MapPost("/api/jobs/{id}/cancel", (string id, Studio s) => ProductionJobFlow.View(s.Cancel(id)));
app.MapPost("/api/jobs/{id}/resume", (string id, Studio s) => ProductionJobFlow.View(s.Resume(id)));
app.MapGet("/api/jobs/{id}/video", (string id, Studio s) =>
{
    var j = s.Job(id);
    if (!ProductionJobFlow.Allows(j.State, "openVideo") || j.Video == null || !File.Exists(j.Video) || MediaTools.Hash(j.Video) != j.VideoHash) throw new NotFoundFault("Verified video not found.");
    return Results.File(j.Video, "video/mp4", enableRangeProcessing: true);
});
app.MapGet("/api/provider", (ApiDirector d) => d.Status);
app.MapPost("/api/provider", (ProviderSettings p, ApiDirector d) => d.Configure(p));
app.MapPost("/api/direct", (DirectRequest r, ApiDirector d, Studio s, DecisionService ds, IHostApplicationLifetime lifetime) => d.Direct(r, s, ds, lifetime.ApplicationStopping));
// Decision layer (Jev): independent provider, per-task policies, catalog
// evidence, candidate decisions and persisted audit records. Proposal
// preparation only; production paths never reach this service.
app.MapGet("/api/decisions/provider", (DecisionService d) => d.ProviderStatus());
app.MapPost("/api/decisions/provider", (DecisionProviderSettings p, DecisionService d) => d.ConfigureProvider(p));
app.MapGet("/api/decisions/policies", (DecisionService d) => d.GetPolicies());
app.MapPost("/api/decisions/policies", (DecisionPolicies p, DecisionService d) => d.ConfigurePolicies(p));
app.MapGet("/api/projects/{id}/catalog", (string id, DecisionService d) => d.CatalogStatus(id));
app.MapGet("/api/projects/{id}/catalog/content", (string id, DecisionService d) => d.ReadCatalog(id));
app.MapPost("/api/projects/{id}/catalog", (string id, PerformanceCatalog catalog, DecisionService d, CancellationToken ct) => d.ImportCatalog(id, catalog, ct));
app.MapPost("/api/projects/{id}/catalog/scaffold", (string id, DecisionService d, CancellationToken ct) => d.ScaffoldCatalog(id, ct));
app.MapGet("/api/projects/{id}/decisions", (string id, DecisionService d) => d.ListRecords(id));
app.MapGet("/api/projects/{id}/decisions/{task}/{requestId}", (string id, string task, string requestId, DecisionService d) => d.ReadRecord(id, task, requestId));
// Paid asks retain their receipts after a disconnected client. Suggestion and
// other bounded preflight/metadata calls remain request-cancellable (no billing).
app.MapPost("/api/projects/{id}/decisions/performance-match", (string id, PerformanceMatchRequest r, DecisionService d, IHostApplicationLifetime lifetime) => d.PerformanceMatch(id, r, lifetime.ApplicationStopping));
app.MapPost("/api/projects/{id}/decisions/camera-choice", (string id, CameraChoiceRequest r, DecisionService d, IHostApplicationLifetime lifetime) => d.CameraChoice(id, r, lifetime.ApplicationStopping));
app.MapPost("/api/projects/{id}/decisions/camera-choice/suggest", (string id, CameraSuggestRequest r, DecisionService d, CancellationToken ct) => d.SuggestCameras(id, r, ct));
app.MapPost("/api/projects/{id}/decisions/audio-match", (string id, AudioMatchRequest r, DecisionService d, IHostApplicationLifetime lifetime) => d.AudioMatch(id, r, lifetime.ApplicationStopping));
app.MapPost("/api/projects/{id}/decisions/film-ranking", (string id, FilmRankingRequest r, DecisionService d, IHostApplicationLifetime lifetime) => d.FilmRanking(id, r, lifetime.ApplicationStopping));
app.MapPost("/api/projects/{id}/decisions/semantic-review", (string id, SemanticReviewRequest r, DecisionService d, IHostApplicationLifetime lifetime) => d.SemanticReview(id, r, lifetime.ApplicationStopping));
app.MapGet("/api/guide", () => Results.Text(DirectorProduct.Guide()));
await app.RunAsync();

// The owning process uses exactly the trusted-client bootstrap, not a token URL.
static async Task OpenBrowser(string address, string token)
{
    try {
        var url = await new WorkbenchConnection(new Uri(address), token).BrowserUrl();
        BrowserLauncher.Open(url, Console.Out);
    }
    catch { Console.WriteLine("Could not open the browser; open from Unity or retry gd workbench --open."); }
}

// Legacy store DETECTION, never migration: user data is not moved silently. The
// split can only exist on macOS (Unity Mono resolved LocalApplicationData to
// ~/.local/share) and only matters when this instance runs on the default store.
static string? LegacyMacStore(string store)
{
    if (!OperatingSystem.IsMacOS()) return null;
    var canonical = GameDirectorPaths.WorkbenchStore();
    if (!SamePath(store, canonical)) return null;
    var legacy = GameDirectorPaths.LegacyMacWorkbenchStore(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    if (SamePath(legacy, canonical)) return null;
    return File.Exists(Path.Combine(legacy, ".identity.json")) && !File.Exists(Path.Combine(canonical, ".identity.json")) ? legacy : null;
}
static bool SamePath(string a, string b) => string.Equals(
    Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
    OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
