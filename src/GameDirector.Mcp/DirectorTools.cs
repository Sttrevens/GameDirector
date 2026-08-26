using System.ComponentModel;
using System.Text;
using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using ModelContextProtocol.Server;

namespace GameDirector.Mcp;

/// <summary>
/// MCP tools mirroring the gd CLI. Tool names use the gd_ prefix so they are
/// distinguishable from engine-level MCPs (e.g. the Unity Editor MCP) that a
/// host may also have connected — the director talks to the *running game*,
 /// the editor MCP talks to the *editor*. Both can coexist in one session.
/// </summary>
[McpServerToolType]
public static class DirectorTools
{
    private const string DefaultEndpoint = "http://127.0.0.1:39777";

    [McpServerTool, Description("Validate a GameDirector timeline JSON against a game capability manifest. Returns compiler diagnostics; zero errors means the timeline is playable. Use this BEFORE gd_play — the compiler is the falsifier that catches unknown roles/clips/locations/shot types.")]
    public static string GdValidateTimeline(
        [Description("Absolute path to the timeline JSON file")] string timelinePath,
        [Description("Absolute path to a manifest JSON. Omit to fetch the live manifest from the running game.")] string? manifestPath = null,
        [Description("Bridge endpoint, used only when manifestPath is omitted")] string endpoint = DefaultEndpoint)
    {
        try
        {
            var timeline = DslJson.Load<TimelineAsset>(timelinePath);
            CapabilityManifest manifest = manifestPath != null
                ? DslJson.Load<CapabilityManifest>(manifestPath)
                : new DirectorBridgeClient(endpoint).GetManifestAsync().GetAwaiter().GetResult();

            var result = TimelineCompiler.Compile(timeline, manifest);
            var sb = new StringBuilder();
            foreach (var d in result.Diagnostics) sb.AppendLine(d.ToString());
            sb.Append(result.HasErrors
                ? "INVALID — fix the errors above and re-validate."
                : $"VALID — '{result.Timeline!.Id}', {result.Timeline.OrderedCues.Count} cues, {result.Timeline.Duration:0.###}s.");
            return sb.ToString();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    [McpServerTool, Description("Fetch the capability manifest from the running game (roles, animation clips, locations, shot vocabulary, audio). Always ground your timeline in this list — never invent ids.")]
    public static async Task<string> GdGetManifest(
        [Description("Bridge endpoint of the running game")] string endpoint = DefaultEndpoint)
    {
        try
        {
            using var client = new DirectorBridgeClient(endpoint);
            if (!await client.IsHealthyAsync()) return Unreachable(endpoint);
            return DslJson.Serialize(await client.GetManifestAsync());
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    [McpServerTool, Description("Play a validated timeline in the running game (director mode). The game compiles server-side again and rejects invalid timelines.")]
    public static async Task<string> GdPlayTimeline(
        [Description("Absolute path to the timeline JSON file")] string timelinePath,
        [Description("Bridge endpoint of the running game")] string endpoint = DefaultEndpoint)
    {
        try
        {
            using var client = new DirectorBridgeClient(endpoint);
            if (!await client.IsHealthyAsync()) return Unreachable(endpoint);
            return await client.PlayAsync(DslJson.Load<TimelineAsset>(timelinePath));
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    [McpServerTool, Description("Stop the currently playing timeline.")]
    public static async Task<string> GdStop([Description("Bridge endpoint")] string endpoint = DefaultEndpoint)
    {
        try
        {
            using var client = new DirectorBridgeClient(endpoint);
            if (!await client.IsHealthyAsync()) return Unreachable(endpoint);
            await client.StopAsync();
            return "stopped";
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    [McpServerTool, Description("Get playback status: player state, timeline time, duration, recent director events.")]
    public static async Task<string> GdStatus([Description("Bridge endpoint")] string endpoint = DefaultEndpoint)
    {
        try
        {
            using var client = new DirectorBridgeClient(endpoint);
            if (!await client.IsHealthyAsync()) return Unreachable(endpoint);
            return await client.GetStatusAsync();
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    [McpServerTool, Description("Capture the current director camera frame to a PNG file for the review loop (LLM watches the take, then revises the timeline).")]
    public static async Task<string> GdCaptureFrame(
        [Description("Absolute output path for the PNG")] string outPath,
        [Description("Bridge endpoint")] string endpoint = DefaultEndpoint)
    {
        try
        {
            using var client = new DirectorBridgeClient(endpoint);
            if (!await client.IsHealthyAsync()) return Unreachable(endpoint);
            await client.CaptureFrameAsync(outPath);
            return "wrote " + outPath;
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    [McpServerTool, Description("Print the GameDirector DSL v0.1 cheat sheet: every cue type, its fields, and the compiler rules. Read this before authoring a timeline.")]
    public static string GdGrammar() =>
        """
        GameDirector DSL v0.1 — cue types (JSON, times in seconds)
          camera.shot      { t, shot:{ type: lockoff|dolly|orbit|tracking|crane, subject, frame, from, to, durationSeconds, fov?, ease?, lookAt?, params? } }
          actor.spawn      { t, role, location, headingTo? }     actor.despawn { t, role }
          actor.anim       { t, role, clip, fade? }              actor.move { t, role, to, speed?, headingTo? }
          actor.face       { t, role, headingTo }                audio.play/stop { t, audioId, volume? }
          world.timescale  { t, scale, duration? } (duration>0 auto-restores)   marker { t, label }
        Compiler rules: all ids must exist in the game manifest; overlapping shots and ops on
        not-yet-spawned roles are warnings; 'current' = reserved camera location.
        Workflow: GdGetManifest -> author timeline -> GdValidateTimeline -> GdPlayTimeline -> GdCaptureFrame -> revise.
        """;

    private static string Unreachable(string endpoint) =>
        $"bridge unreachable at {endpoint}. Start the game in director mode (M1: playmode with the GameDirector bridge enabled), then retry.";
}
