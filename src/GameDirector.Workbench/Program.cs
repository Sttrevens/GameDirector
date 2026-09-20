using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Workbench;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
var port = builder.Configuration.GetValue("port", BridgeDefaults.WorkbenchPort);
// Body ceiling derives from the largest legitimate upload: one base64-encoded
// sound import plus JSON envelope slack. Never an independent literal.
var maxBodyBytes = (long)(FilmLimits.MaxAudioImportBytes * 4L / 3) + 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = maxBodyBytes; });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; });
builder.Services.AddSingleton(new Studio(builder.Configuration["data"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDirector", "Workbench")));
builder.Services.AddHostedService(s => s.GetRequiredService<Studio>());
builder.Services.AddSingleton<ApiDirector>();
var app = builder.Build();
var instanceId = Guid.NewGuid().ToString("N");
var authority = "127.0.0.1:" + port;
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.Single(); authority = new Uri(address).Authority;
    var studio = app.Services.GetRequiredService<Studio>();
    DslJson.SaveAtomic(studio.StorePath(".service.json"), new { product = "GameDirector", protocolVersion = 1, instanceId, studio.StoreId, address, processId = Environment.ProcessId });
    if (builder.Configuration.GetValue("open", false))
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
        catch { /* The printed local URL remains usable in headless environments. */ }
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
        if (context.Request.Method != "GET" && !context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
    }
    try { await next(); }
    catch (DocumentConflict ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { code = "document_conflict", error = ex.Message, current = ex.Current });
    }
    catch (RevisionRequired ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 428;
        await context.Response.WriteAsJsonAsync(new { code = "revision_required", error = ex.Message });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = ex is KeyNotFoundException ? 404 : ex is InvalidOperationException ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/health", (Studio s) => new { product = "GameDirector", protocolVersion = 1, instanceId, s.StoreId, features = new[] { "film-documents-v1", "production-readiness" },
    engines = EngineProfiles.All,
    defaults = new { frameRate = FilmDefaults.FrameRate, width = FilmDefaults.Width, height = FilmDefaults.Height },
    limits = new { scenes = FilmLimits.MaxScenes, shots = FilmLimits.MaxShots, audioCues = FilmLimits.MaxAudioCues, subtitles = FilmLimits.MaxSubtitles,
        maxAudioBytes = MediaFormats.MaxImportBytes, maxScriptBytes = FilmLimits.MaxScriptBytes, maxAudioFormats = MediaFormats.AudioExtensions, outputSeconds = FilmLimits.MaxOutputSeconds } });
app.MapGet("/api/doctor", (Studio s, CancellationToken ct) => DirectorProduct.Doctor(null, ct));
// Media bootstrap: explicit local user action only; options are platform-filtered.
app.MapGet("/api/media/install", () => MediaToolInstaller.Status());
app.MapPost("/api/media/install", (MediaInstallRequest r, Studio s, CancellationToken ct) => MediaToolInstaller.Install(s, r.OptionId, ct));
app.MapPost("/api/folders", (FolderRequest request) => ProjectFolders.Browse(request));
app.MapGet("/api/projects/{id}/media", (string id, Studio s) => MediaLibrary.List(s, id));
app.MapPost("/api/projects/{id}/media", (string id, MediaImport input, Studio s, CancellationToken ct) => MediaLibrary.Import(s, id, input, ct));
app.MapGet("/api/storage", (Studio s) => s.StorageStatus());
app.MapGet("/api/state", (Studio s) => s.Snapshot());
app.MapPost("/api/projects", (StudioProject p, Studio s) => s.Register(p));
app.MapPost("/api/projects/{id}/connect", (string id, Studio s, CancellationToken ct) => s.Connect(id, ct));
app.MapGet("/api/projects/{id}/starter", (string id, Studio s) => FilmCompiler.Starter(s.Project(id).Manifest ?? throw new InvalidOperationException("Connect the engine first.")));
// Legacy reads remain useful; unconditional legacy writes cannot bypass revisions.
app.MapGet("/api/projects/{id}/draft", (string id, Studio s) => Results.Json(s.Documents.Read(id, "default").Film));
app.MapPost("/api/projects/{id}/draft", (string id, Studio s) => { s.Project(id); throw new RevisionRequired(); });
app.MapGet("/api/projects/{id}/documents", (string id, Studio s) => s.Documents.List(id));
app.MapGet("/api/projects/{id}/documents/{documentId}", (string id, string documentId, long? revision, Studio s) => s.Documents.Read(id, documentId, revision));
app.MapPost("/api/projects/{id}/documents/{documentId}", (string id, string documentId, SaveFilmDocument input, Studio s) => s.Documents.Save(id, documentId, input));
app.MapPost("/api/projects/{id}/documents/{documentId}/readiness", (string id, string documentId, CheckFilmDocument input, Studio s, CancellationToken ct) =>
    ProductionReadiness.Check(s, id, FilmDocuments.ProductionFilm(s.Documents.Read(id, documentId, input.Revision), input.Preview), ct));
app.MapPost("/api/projects/{id}/documents/{documentId}/produce", (string id, string documentId, ProduceFilmDocument input, Studio s, CancellationToken ct) =>
    s.Submit(new ProductionRequest { ProjectId = id, RequestId = input.RequestId, DocumentId = documentId, DocumentRevision = input.Revision,
        Film = FilmDocuments.ProductionFilm(s.Documents.Read(id, documentId, input.Revision), input.Preview) }, ct));
app.MapPost("/api/validate", async (ProductionRequest r, Studio s, CancellationToken ct) => { var f = await s.Validate(r, ct); return new { valid = true, manifestHash = f.ManifestHash, shots = f.Shots.Select(x => new { x.Id, x.Purpose, x.Start, x.End, x.CacheKey }), duration = f.Shots.Sum(x => x.End - x.Start) }; });
app.MapPost("/api/jobs", (ProductionRequest r, Studio s, CancellationToken ct) => s.Submit(r, ct));
app.MapGet("/api/jobs/{id}", (string id, Studio s) => s.Job(id));
app.MapPost("/api/jobs/{id}/cancel", (string id, Studio s) => s.Cancel(id));
app.MapPost("/api/jobs/{id}/resume", (string id, Studio s) => s.Resume(id));
app.MapGet("/api/jobs/{id}/video", (string id, Studio s) =>
{
    var j = s.Job(id);
    if (j.State != "Verified" || j.Video == null || !File.Exists(j.Video) || MediaTools.Hash(j.Video) != j.VideoHash) return Results.NotFound();
    return Results.File(j.Video, "video/mp4", enableRangeProcessing: true);
});
app.MapGet("/api/provider", (ApiDirector d) => d.Status);
app.MapPost("/api/provider", (ProviderSettings p, ApiDirector d) => d.Configure(p));
app.MapPost("/api/direct", (DirectRequest r, ApiDirector d, Studio s, CancellationToken ct) => d.Direct(r, s, ct));
app.MapGet("/api/guide", () => Results.Text(DirectorProduct.Guide()));
await app.RunAsync();
