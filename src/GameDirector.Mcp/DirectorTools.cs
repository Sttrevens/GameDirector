using System.ComponentModel;
using System.Text;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Core.Compilation;
using ModelContextProtocol.Server;

namespace GameDirector.Mcp;

/// <summary>
/// MCP tools mirroring the gd CLI. Tool names are pinned by McpToolNames
/// constants (the generated grammar and the tools/list snapshot bind to the
/// same constants) and use the gd_ prefix so they are distinguishable from
/// engine-level MCPs (e.g. the Unity Editor MCP) that a host may also have
/// connected — the director talks to the *running game*, the editor MCP talks
/// to the *editor*. Both can coexist in one session.
/// </summary>
[McpServerToolType]
public static class DirectorTools
{
    private static string Resolve(string? endpoint) => endpoint ?? Environment.GetEnvironmentVariable("GAMEDIRECTOR_ENDPOINT") ?? BridgeDefaults.UnityEndpoint;

    [McpServerTool(Name = McpToolNames.ValidateTimeline, ReadOnly = true), Description("Validate a GameDirector timeline JSON against a game capability manifest. Returns compiler diagnostics; zero errors confirms the declared vocabulary and structure only; the live engine still checks actual bindings and capture prerequisites. Use this BEFORE gd_play_timeline — the compiler is the falsifier that catches unknown roles/clips/locations/shot types.")]
    public static async Task<string> GdValidateTimeline(
        [Description("Absolute path to the timeline JSON file")] string timelinePath,
        [Description("Absolute path to a manifest JSON. Omit to fetch the live manifest from the running game.")] string? manifestPath = null,
        [Description("Bridge endpoint, used only when manifestPath is omitted")] string? endpoint = null,
        CancellationToken cancellationToken = default,
        [Description("Explicit capture width in pixels; with aspect hints, supply both width and height")] int? width = null,
        [Description("Explicit capture height in pixels; with aspect hints, supply both width and height")] int? height = null)
    {
        var result = await DirectorOperations.ValidateTimeline(timelinePath, manifestPath, Resolve(endpoint), cancellationToken, width, height);
        var sb = new StringBuilder();
        foreach (var d in result.Diagnostics) sb.AppendLine(d.ToString());
        sb.Append(result.HasErrors
            ? "INVALID — fix the errors above and re-validate."
            : $"VALID — '{result.Timeline!.Id}', {result.Timeline.OrderedCues.Count} cues, {result.Timeline.Duration:0.###}s.");
        return sb.ToString();
    }

    [McpServerTool(Name = McpToolNames.GetManifest, ReadOnly = true), Description("Fetch the capability manifest from the running game (roles, animation clips, locations, shot vocabulary, audio). Always ground your timeline in this list — never invent ids.")]
    public static async Task<string> GdGetManifest(
        [Description("Bridge endpoint of the running game")] string? endpoint = null,
        CancellationToken cancellationToken = default) =>
        DslJson.Serialize(await DirectorOperations.WithReachableBridge(Resolve(endpoint), c => c.GetManifestAsync(cancellationToken), cancellationToken));

    [McpServerTool(Name = McpToolNames.PlayTimeline), Description("Play a validated timeline in the running game (director mode). Hinted timelines are refused client-side with GD1013; use gd_take with explicit size. The game compiles server-side again and rejects invalid timelines.")]
    public static async Task<string> GdPlayTimeline(
        [Description("Absolute path to the timeline JSON file")] string timelinePath,
        [Description("Bridge endpoint of the running game")] string? endpoint = null,
        CancellationToken cancellationToken = default) =>
        await DirectorOperations.WithReachableBridge(Resolve(endpoint),
            c => c.PlayAsync(AspectHints.Resolve(DslJson.LoadAuthored<TimelineAsset>(timelinePath)), cancellationToken), cancellationToken);

    [McpServerTool(Name = McpToolNames.Stop, Idempotent = true), Description("Stop the currently playing timeline.")]
    public static async Task<string> GdStop(
        [Description("Bridge endpoint of the running game")] string? endpoint = null,
        CancellationToken cancellationToken = default)
    {
        await DirectorOperations.WithReachableBridge(Resolve(endpoint), c => c.StopAsync(cancellationToken), cancellationToken);
        return "stopped";
    }

    [McpServerTool(Name = McpToolNames.Status, ReadOnly = true), Description("Get playback status: player state, timeline time, duration, recent director events.")]
    public static async Task<string> GdStatus(
        [Description("Bridge endpoint of the running game")] string? endpoint = null,
        CancellationToken cancellationToken = default) =>
        DslJson.Serialize(await DirectorOperations.WithReachableBridge(Resolve(endpoint), c => c.GetStatusAsync(cancellationToken), cancellationToken));

    [McpServerTool(Name = McpToolNames.CaptureFrame), Description("Capture the current director camera frame to a PNG file for the review loop (LLM watches the take, then revises the timeline).")]
    public static async Task<string> GdCaptureFrame(
        [Description("Absolute output path for the PNG")] string outPath,
        [Description("Bridge endpoint of the running game")] string? endpoint = null,
        CancellationToken cancellationToken = default)
    {
        await DirectorOperations.WithReachableBridge(Resolve(endpoint), async c => {
            var path = OutputPolicy.RequireOutput(outPath, OutputPolicy.ManifestRoot(await c.GetManifestAsync(cancellationToken)));
            var bytes = await c.CaptureFrameAsync(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        }, cancellationToken);
        return "wrote " + outPath;
    }

    [McpServerTool(Name = McpToolNames.Grammar, ReadOnly = true), Description("Print the GameDirector DSL quick reference, generated from the same Core that enforces it: every cue type, its fields, the shot vocabulary and defaults, capabilities and the compiler rules. Read this before authoring a timeline.")]
    public static string GdGrammar() => DirectorProduct.Grammar();

    [McpServerTool(Name = McpToolNames.Take), Description("Record a repeatable frame-stepped game take, verify every video frame, and write picture.mp4 plus provenance receipts. Output directory must be new. Picture capture has no audio; mix a soundtrack with gd_edit.")]
    public static async Task<string> GdTake(
        [Description("Absolute path to the timeline JSON file")] string timelinePath,
        [Description("New output directory for the take artifacts")] string outputDirectory,
        [Description("Bridge endpoint of the running game")] string? endpoint = null,
        [Description("Capture frame rate (frames per second)")] int frameRate = FilmDefaults.FrameRate,
        [Description("Capture width in pixels (even)")] int width = FilmDefaults.Width,
        [Description("Capture height in pixels (even)")] int height = FilmDefaults.Height,
        CancellationToken cancellationToken = default)
        => await TakeRecorder.Record(timelinePath, outputDirectory, Resolve(endpoint), frameRate, width, height, ct: cancellationToken);

    [McpServerTool(Name = McpToolNames.Edit), Description("Render a game-neutral edit decision list with exact frame-aligned source ranges, cut reasons, grading, soundtrack and subtitles. Verifies source hashes and final frame count. Output directory must be new.")]
    public static async Task<string> GdEdit(
        [Description("Absolute path to the edit decision list JSON")] string editPath,
        [Description("New output directory for the rendered delivery")] string outputDirectory,
        CancellationToken cancellationToken = default)
        => await EditRenderer.Render(editPath, outputDirectory, cancellationToken);

    [McpServerTool(Name = McpToolNames.DirectorGuide, ReadOnly = true), Description("Read the complete game-neutral director workflow: evidence-backed performance scouting, narrative, rehearsal, capture, sound, review and delivery. Start here when working with a new game.")]
    public static string GdDirectorGuide() => DirectorProduct.Guide();

    [McpServerTool(Name = McpToolNames.Doctor, ReadOnly = true), Description("Check installed media dependencies and optionally the live game bridge. This checks setup, not visual acceptance.")]
    public static async Task<string> GdDoctor(
        [Description("Bridge endpoint of the running game; omit to skip the bridge check")] string? endpoint = null,
        CancellationToken cancellationToken = default) =>
        DslJson.Serialize(await DirectorProduct.Doctor(endpoint, cancellationToken));

    [McpServerTool(Name = McpToolNames.PerformanceCatalog, ReadOnly = true), Description("Read a performance catalog grounded in an exact manifest and design/observed evidence. Rejects stale or unbound meanings; unknown meanings require scouting.")]
    public static string GdPerformanceCatalog(
        [Description("Absolute path to the performance catalog JSON")] string catalogPath,
        [Description("Absolute path to the manifest JSON the catalog is stamped against")] string manifestPath) =>
        DslJson.Serialize(CatalogTools.Inspect(catalogPath, manifestPath));

    [McpServerTool(Name = McpToolNames.Resume), Description("Recover a persisted take: re-encode completed hashed frames without the engine, or replay incomplete work into a separate attempt after checking source identity. Never mixes old and new pixels.")]
    public static async Task<string> GdResume(
        [Description("The take's output directory from a previous gd_take")] string outputDirectory,
        CancellationToken cancellationToken = default) =>
        (await TakeRecorder.Resume(outputDirectory, ct: cancellationToken)).Video;
}
