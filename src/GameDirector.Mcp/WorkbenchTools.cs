using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using GameDirector.Client;
using GameDirector.Decisions;
using ModelContextProtocol.Server;

namespace GameDirector.Mcp;

/// <summary>
/// Managed-production tools over the local Workbench. Read-only queries and
/// mutating actions are separate tools on purpose — a host can gate change
/// without parsing a free-string action parameter.
/// </summary>
[McpServerToolType]
public static class WorkbenchTools
{
    private static async Task<string> Call(string path, object? body = null, int timeoutSeconds = 60)
    {
        // One discovery path: env override, then the running instance's service
        // descriptor (a Unity-launched Workbench always uses --port 0), then the default port.
        var endpoint = await WorkbenchLocator.ResolveAsync();
        using var http = endpoint.CreateClient(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = body == null ? await http.GetAsync("api/" + path) : await http.PostAsJsonAsync("api/" + path, body, DslJson.Options);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new WorkbenchApiException(response.StatusCode, text);
            return text;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            // Name the endpoint actually dialed; the socket error only hints at it.
            throw new HttpRequestException("cannot reach " + endpoint + " (" + ex.Message + ")", ex);
        }
    }

    private static string ProjectPath(string projectId, string tail = "") =>
        "projects/" + Uri.EscapeDataString(projectId) + tail;

    [McpServerTool(Name = McpToolNames.StudioState, ReadOnly = true), Description("Read the local director Workbench projects, asset inventories, live manifests and persistent film jobs. Run the Workbench first. All gd_studio tools share state with its UI; use these tools for managed production.")]
    public static Task<string> GdStudioState() => Call("state");

    [McpServerTool(Name = McpToolNames.StudioDecision), Description("Ask the configured Jev provider to match performances/sounds, choose a camera for one stable shot ID, rank candidate films, or run text-only semantic review checks on a supplied text. requestPath is a local JSON request with a stable requestId, intent and task-specific candidates/document revision. This only prepares an audited proposal: it never saves the document or starts production. Read decisions policies and catalog first; needs-review/no-match preserve the original film.")]
    public static Task<string> GdStudioDecision(
        [Description("Workbench project id")] string projectId,
        [Description("Supported decision task id: " + DecisionTasks.PerformanceMatch + " | " + DecisionTasks.CameraChoice + " | " + DecisionTasks.AudioMatch + " | " + DecisionTasks.FilmRanking + " | " + DecisionTasks.SemanticReview)] string task,
        [Description("Absolute path to a local decision request JSON (bounded in size, object shaped)")] string requestPath)
    {
        if (!DecisionTasks.All.Contains(task)) throw new ArgumentException("Supported tasks: " + string.Join(", ", DecisionTasks.All));
        var file = new FileInfo(requestPath);
        if (!file.Exists || file.Length > GameDirector.Core.Dsl.FilmLimits.MaxScriptBytes)
            throw new ArgumentException("Choose an existing bounded decision request JSON file.");
        var request = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file.FullName));
        if (request.ValueKind != JsonValueKind.Object) throw new ArgumentException("Decision request must be a JSON object.");
        return Call(ProjectPath(projectId, "/decisions/" + task), request, DecisionPolicyBounds.TimeoutSeconds + 30);
    }

    [McpServerTool(Name = McpToolNames.StudioDecisionRecords, ReadOnly = true), Description("Read a project's decision audit records, or one prior result using both task and requestId. Results include offered candidates, manifest/evidence hashes, provider answers and proposal. Reuse the same requestId only for identical inputs.")]
    public static Task<string> GdStudioDecisionRecords(
        [Description("Workbench project id")] string projectId,
        [Description("Decision task id (" + DecisionTasks.PerformanceMatch + " | " + DecisionTasks.CameraChoice + " | " + DecisionTasks.AudioMatch + " | " + DecisionTasks.FilmRanking + " | " + DecisionTasks.SemanticReview + "); supply together with requestId, or omit both to list records")] string? task = null,
        [Description("Stable request id of the original decision call")] string? requestId = null)
    {
        if ((task == null) != (requestId == null)) throw new ArgumentException("Supply both task and requestId, or neither to list records.");
        var path = ProjectPath(projectId, "/decisions");
        return Call(task == null ? path : path + "/" + Uri.EscapeDataString(task) + "/" + Uri.EscapeDataString(requestId!));
    }

    [McpServerTool(Name = McpToolNames.StudioDecisionSettings, ReadOnly = true), Description("Read decision provider status and per-task acceptance policies without returning credentials. Configure provider credentials in the local Workbench settings.")]
    public static Task<string> GdStudioDecisionSettings() => Call("decisions/provider");

    [McpServerTool(Name = McpToolNames.StudioCatalogStatus, ReadOnly = true), Description("Report a project's performance catalog status and manifest freshness. A stale catalog requires re-checking observations before decisions may consume it.")]
    public static Task<string> GdStudioCatalogStatus(
        [Description("Workbench project id")] string projectId) =>
        Call(ProjectPath(projectId, "/catalog"));

    [McpServerTool(Name = McpToolNames.StudioCatalogRead, ReadOnly = true), Description("Read a project's performance catalog content: authored intent and observed evidence, kept separate — never infer performance from filenames.")]
    public static Task<string> GdStudioCatalogRead(
        [Description("Workbench project id")] string projectId) =>
        Call(ProjectPath(projectId, "/catalog/content"));

    [McpServerTool(Name = McpToolNames.StudioCatalogScaffold, Idempotent = true), Description("Scaffold an evidence-backed performance catalog, discovering real actor/clip IDs with unknown meaning. An existing catalog is preserved untouched; replace only through an explicit gd_studio_catalog_import.")]
    public static Task<string> GdStudioCatalogScaffold(
        [Description("Workbench project id")] string projectId) =>
        Call(ProjectPath(projectId, "/catalog/scaffold"), new { });

    [McpServerTool(Name = McpToolNames.StudioCatalogImport, Destructive = true), Description("Import a performance catalog from a local JSON file, replacing the project's current catalog. Describe actual observations and authored intent separately, never infer performance from filenames.")]
    public static Task<string> GdStudioCatalogImport(
        [Description("Workbench project id")] string projectId,
        [Description("Absolute path to the performance catalog JSON to import")] string catalogPath) =>
        Call(ProjectPath(projectId, "/catalog"), DslJson.LoadAuthored<PerformanceCatalog>(catalogPath));

    [McpServerTool(Name = McpToolNames.StudioDocument, ReadOnly = true), Description("Read versioned film documents shared with Unity Inspector and the browser. Omit documentId to list a project's films; supply it to read the current film and revision.")]
    public static Task<string> GdStudioDocument(
        [Description("Workbench project id")] string projectId,
        [Description("Film document id; omit to list the project's documents")] string? documentId = null) =>
        Call(ProjectPath(projectId, "/documents" + (documentId == null ? "" : "/" + Uri.EscapeDataString(documentId))));

    [McpServerTool(Name = McpToolNames.StudioSaveDocument, Idempotent = true), Description("Save a new immutable film revision. expectedRevision must be the revision actually read (0 for a new document). Reuse requestId and identical inputs after a lost response. A conflict preserves the other client's edits; read and reconcile instead of overwriting.")]
    public static Task<string> GdStudioSaveDocument(
        [Description("Workbench project id")] string projectId,
        [Description("Film document id")] string documentId,
        [Description("Revision last read (0 for a new document); a mismatch is a conflict, not an overwrite")] long expectedRevision,
        [Description("Stable id for this save attempt; reused only with identical inputs")] string requestId,
        [Description("Absolute path to the version 1 FilmPlan JSON file")] string filmPath) =>
        Call(ProjectPath(projectId, "/documents/" + Uri.EscapeDataString(documentId)), new { expectedRevision, requestId, film = FilmCompiler.Read(filmPath) });

    [McpServerTool(Name = McpToolNames.StudioDocumentReadiness, ReadOnly = true), Description("Check production readiness of an exact saved document revision: compile evidence, media bindings and source freshness, without starting production.")]
    public static Task<string> GdStudioDocumentReadiness(
        [Description("Workbench project id")] string projectId,
        [Description("Film document id")] string documentId,
        [Description("Immutable saved revision to check")] long revision,
        [Description("True to assess the half-resolution preview rendition")] bool preview = false) =>
        Call(ProjectPath(projectId, "/documents/" + Uri.EscapeDataString(documentId) + "/readiness"), new { revision, preview });

    [McpServerTool(Name = McpToolNames.StudioDocumentProduce, Idempotent = true), Description("Produce an exact saved document revision into picture. Production uses an immutable snapshot, shared with Unity Inspector and browser. Reuse the SAME requestId on retries; use a NEW requestId for a revised film.")]
    public static Task<string> GdStudioDocumentProduce(
        [Description("Workbench project id")] string projectId,
        [Description("Film document id")] string documentId,
        [Description("Immutable saved revision to produce")] long revision,
        [Description("Stable id for this production request; reused only on network retry")] string requestId,
        [Description("True to produce the half-resolution preview rendition")] bool preview = false) =>
        Call(ProjectPath(projectId, "/documents/" + Uri.EscapeDataString(documentId) + "/produce"), new { revision, preview, requestId });

    [McpServerTool(Name = McpToolNames.StudioProject), Description("Register a project in the local director Workbench and refresh its live manifest. endpoint must be a loopback presentation bridge. Asset discovery and performance readiness are distinct.")]
    public static async Task<string> GdStudioProject(
        [Description("Stable project id")] string id,
        [Description("Human-readable project name")] string name,
        [Description("Engine label (e.g. Unity, Three.js)")] string engine,
        [Description("Loopback presentation bridge endpoint of the running game")] string endpoint)
    {
        await Call("projects", new { id, name, engine, endpoint });
        return await Call(ProjectPath(id, "/connect"), new { });
    }

    [McpServerTool(Name = McpToolNames.StudioValidate, ReadOnly = true), Description("Validate a version 1 FilmPlan file against a Workbench project's LIVE manifest. A film has scenes with shared performance cues and shots (stable id, start, end, purpose, camera). Supports imported audio cue IDs and final-film captions; speech synthesis and lip sync are not available.")]
    public static Task<string> GdStudioValidate(
        [Description("Workbench project id")] string projectId,
        [Description("Absolute path to the version 1 FilmPlan JSON file")] string filmPath) =>
        Call("validate", new { projectId, film = FilmCompiler.Read(filmPath) });

    [McpServerTool(Name = McpToolNames.StudioProduce, Idempotent = true), Description("Submit a version 1 FilmPlan file to the local Workbench. Returns a persistent job immediately; follow with gd_studio_job_status. Reuse the SAME requestId for a network retry, use a NEW requestId for a revised film. Unchanged shots reuse verified pixels. Live source drift rejects stale work. No shell commands or arbitrary engine code are executed.")]
    public static Task<string> GdStudioProduce(
        [Description("Workbench project id")] string projectId,
        [Description("Absolute path to the version 1 FilmPlan JSON file")] string filmPath,
        [Description("Stable id for this production request; reused only on network retry")] string requestId) =>
        Call("jobs", new { projectId, requestId, film = FilmCompiler.Read(filmPath) });

    [McpServerTool(Name = McpToolNames.StudioMediaList, ReadOnly = true), Description("List a project's available immutable media IDs for use in FilmPlan.audio.")]
    public static Task<string> GdStudioMediaList(
        [Description("Workbench project id")] string projectId) =>
        Call(ProjectPath(projectId, "/media"));

    [McpServerTool(Name = McpToolNames.StudioMediaImport), Description("Import a local audio file into a project's external Workbench store. Input files are read only. WAV/MP3/M4A/OGG/FLAC, up to 5 MiB. Use returned IDs in FilmPlan.audio; model output cannot read arbitrary filesystem paths.")]
    public static async Task<string> GdStudioMediaImport(
        [Description("Workbench project id")] string projectId,
        [Description("Absolute path to the audio file (WAV/MP3/M4A/OGG/FLAC, up to 5 MiB)")] string filePath)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists || file.Length > GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes)
            throw new ArgumentException("Choose an existing audio file up to " + GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes / (1024 * 1024) + " MiB.");
        MediaFormats.RequireAudio(file.Name);
        return await Call(ProjectPath(projectId, "/media"), new { name = file.Name, base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName)) });
    }

    [McpServerTool(Name = McpToolNames.StudioJobStatus, ReadOnly = true), Description("Read a persistent Workbench film job: state machine position, per-shot progress and receipts.")]
    public static Task<string> GdStudioJobStatus(
        [Description("Job id returned by gd_studio_produce or gd_studio_document_produce")] string jobId) =>
        Call("jobs/" + Uri.EscapeDataString(jobId));

    [McpServerTool(Name = McpToolNames.StudioJobCancel, Destructive = true), Description("Cancel a persistent Workbench film job. Completed verified takes are retained; the job cannot be produced further except through gd_studio_job_resume.")]
    public static Task<string> GdStudioJobCancel(
        [Description("Job id to cancel")] string jobId) =>
        Call("jobs/" + Uri.EscapeDataString(jobId) + "/cancel", new { });

    [McpServerTool(Name = McpToolNames.StudioJobResume), Description("Resume an interrupted or cancelled Workbench film job. Resuming retains the original script/source identity and completed verified takes.")]
    public static Task<string> GdStudioJobResume(
        [Description("Job id to resume")] string jobId) =>
        Call("jobs/" + Uri.EscapeDataString(jobId) + "/resume", new { });
}
