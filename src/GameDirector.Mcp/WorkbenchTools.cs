using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using GameDirector.Client;
using ModelContextProtocol.Server;

namespace GameDirector.Mcp;

[McpServerToolType]
public static class WorkbenchTools
{
    private static async Task<string> Call(string path, object? body = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH") ?? GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback) throw new ArgumentException("Workbench must be a local HTTP endpoint.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        using var response = body == null ? await http.GetAsync("api/" + path) : await http.PostAsJsonAsync("api/" + path, body, DslJson.Options);
        var text = await response.Content.ReadAsStringAsync();
        return response.IsSuccessStatusCode ? text : "ERROR: " + text;
    }
    [McpServerTool, Description("Read the local director Workbench projects, asset inventories, live manifests and persistent film jobs. Run the Workbench first. All gd_studio tools share state with its UI; use these tools for managed production.")]
    public static Task<string> GdStudioState() => Call("state");

    [McpServerTool, Description("Read versioned film documents shared with Unity Inspector and the browser. Omit documentId to list a project's films; supply it to read the current film and revision.")]
    public static Task<string> GdStudioDocument(string projectId, string? documentId = null) =>
        Call("projects/" + Uri.EscapeDataString(projectId) + "/documents" + (documentId == null ? "" : "/" + Uri.EscapeDataString(documentId)));

    [McpServerTool, Description("Save a new immutable film revision. expectedRevision must be the revision actually read (0 for a new document). Reuse requestId and identical inputs after a lost response. A conflict preserves the other client's edits; read and reconcile instead of overwriting.")]
    public static Task<string> GdStudioSaveDocument(string projectId, string documentId, long expectedRevision, string requestId, string filmPath) =>
        Call("projects/" + Uri.EscapeDataString(projectId) + "/documents/" + Uri.EscapeDataString(documentId), new { expectedRevision, requestId, film = FilmCompiler.Read(filmPath) });

    [McpServerTool, Description("Check readiness or produce an exact saved document revision. action is readiness or produce. Production uses an immutable snapshot, shared with Unity Inspector and browser. Reuse requestId on retries.")]
    public static Task<string> GdStudioDocumentProduction(string projectId, string documentId, long revision, string action = "readiness", string requestId = "", bool preview = false)
    {
        var path = "projects/" + Uri.EscapeDataString(projectId) + "/documents/" + Uri.EscapeDataString(documentId);
        return action switch {
            "readiness" => Call(path + "/readiness", new { revision, preview }),
            "produce" => Call(path + "/produce", new { revision, preview, requestId }),
            _ => throw new ArgumentException("action must be readiness or produce")
        };
    }

    [McpServerTool, Description("Register a project in the local director Workbench and refresh its live manifest. endpoint must be a loopback presentation bridge. Asset discovery and performance readiness are distinct.")]
    public static async Task<string> GdStudioProject(string id, string name, string engine, string endpoint)
    {
        var register = await Call("projects", new { id, name, engine, endpoint });
        if (register.StartsWith("ERROR:")) return register;
        return await Call("projects/" + Uri.EscapeDataString(id) + "/connect", new { });
    }
    [McpServerTool, Description("Validate a version 1 FilmPlan file against a Workbench project's LIVE manifest. A film has scenes with shared performance cues and shots (stable id, start, end, purpose, camera). Supports imported audio cue IDs and final-film captions; speech synthesis and lip sync are not available.")]
    public static Task<string> GdStudioValidate(string projectId, string filmPath) => Call("validate", new { projectId, film = FilmCompiler.Read(filmPath) });

    [McpServerTool, Description("Submit a version 1 FilmPlan file to the local Workbench. Returns a persistent job immediately; follow with gd_studio_job. Reuse the SAME requestId for a network retry, use a NEW requestId for a revised film. Unchanged shots reuse verified pixels. Live source drift rejects stale work. No shell commands or arbitrary engine code are executed.")]
    public static Task<string> GdStudioProduce(string projectId, string filmPath, string requestId) => Call("jobs", new { projectId, requestId, film = FilmCompiler.Read(filmPath) });

    [McpServerTool, Description("Import a local audio file into a project's external Workbench store, or list available immutable media IDs if filePath is omitted. Input files are read only. WAV/MP3/M4A/OGG/FLAC, up to 5 MiB. Use returned IDs in FilmPlan.audio; model output cannot read arbitrary filesystem paths.")]
    public static async Task<string> GdStudioMedia(string projectId, string? filePath = null)
    {
        var path = "projects/" + Uri.EscapeDataString(projectId) + "/media";
        if (filePath == null) return await Call(path);
        var file = new FileInfo(filePath);
        if (!file.Exists || file.Length > 5 * 1024 * 1024) throw new ArgumentException("Choose an existing audio file up to 5 MiB.");
        return await Call(path, new { name = file.Name, base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName)) });
    }
    [McpServerTool, Description("Inspect, cancel or resume a persistent Workbench film job. action: status, cancel, resume. Resuming retains original script/source identity and completed takes.")]
    public static Task<string> GdStudioJob(string jobId, string action = "status") => action switch {
        "status" => Call("jobs/" + Uri.EscapeDataString(jobId)),
        "cancel" or "resume" => Call("jobs/" + Uri.EscapeDataString(jobId) + "/" + action, new { }),
        _ => throw new ArgumentException("action must be status, cancel or resume")
    };
}
