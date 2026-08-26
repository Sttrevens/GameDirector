using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Cli;

/// <summary>
/// gd — GameDirector command line. Zero-dependency arg parsing on purpose:
/// this tool must stay trivially auditable by the LLMs that drive it.
///
///   gd validate &lt;timeline.json&gt; [--manifest m.json | --endpoint url]
///   gd manifest [--endpoint url]              (print the live game manifest)
///   gd play &lt;timeline.json&gt; [--endpoint url]
///   gd stop | status [--endpoint url]
///   gd capture --out frame.png [--endpoint url]
///   gd grammar                                (print the DSL cheat sheet)
///
/// Exit codes: 0 ok · 1 validation/usage failure · 2 bridge unreachable.
/// </summary>
public static class Program
{
    private const string DefaultEndpoint = "http://127.0.0.1:39777";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return 0; }

        string command = args[0];
        string? positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string endpoint = Option(args, "--endpoint") ?? DefaultEndpoint;
        string? manifestPath = Option(args, "--manifest");
        string? outPath = Option(args, "--out");

        try
        {
            switch (command)
            {
                case "validate":
                {
                    if (positional == null) return Fail("validate requires a timeline path");
                    var timeline = DslJson.Load<TimelineAsset>(positional);
                    var manifest = manifestPath != null
                        ? DslJson.Load<CapabilityManifest>(manifestPath)
                        : await new DirectorBridgeClient(endpoint).GetManifestAsync();
                    var result = TimelineCompiler.Compile(timeline, manifest);
                    foreach (var d in result.Diagnostics) Console.WriteLine(d.ToString());
                    if (result.HasErrors) { Console.Error.WriteLine($"INVALID: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)"); return 1; }
                    Console.WriteLine($"VALID: '{result.Timeline!.Id}' — {result.Timeline.OrderedCues.Count} cues, duration {result.Timeline.Duration:0.###}s");
                    return 0;
                }

                case "manifest":
                {
                    using var client = await Reachable(endpoint);
                    Console.WriteLine(DslJson.Serialize(await client.GetManifestAsync()));
                    return 0;
                }

                case "play":
                {
                    if (positional == null) return Fail("play requires a timeline path");
                    using var client = await Reachable(endpoint);
                    var timeline = DslJson.Load<TimelineAsset>(positional);
                    Console.WriteLine(await client.PlayAsync(timeline));
                    return 0;
                }

                case "stop":
                {
                    using var client = await Reachable(endpoint);
                    await client.StopAsync();
                    Console.WriteLine("stopped");
                    return 0;
                }

                case "status":
                {
                    using var client = await Reachable(endpoint);
                    Console.WriteLine(await client.GetStatusAsync());
                    return 0;
                }

                case "capture":
                {
                    if (outPath == null) return Fail("capture requires --out <path.png>");
                    using var client = await Reachable(endpoint);
                    await client.CaptureFrameAsync(outPath);
                    Console.WriteLine("wrote " + outPath);
                    return 0;
                }

                case "grammar":
                    Grammar();
                    return 0;

                default:
                    return Fail($"unknown command '{command}'");
            }
        }
        catch (HttpRequestException) { Console.Error.WriteLine($"bridge unreachable at {endpoint} (is the game running in director mode?)"); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static async Task<DirectorBridgeClient> Reachable(string endpoint)
    {
        var client = new DirectorBridgeClient(endpoint);
        if (!await client.IsHealthyAsync())
        {
            client.Dispose();
            throw new HttpRequestException("unreachable");
        }
        return client;
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); Usage(); return 1; }

    private static void Usage() => Console.WriteLine("""
        gd — GameDirector CLI
          gd validate <timeline.json> [--manifest m.json | --endpoint url]
          gd manifest [--endpoint url]
          gd play <timeline.json> [--endpoint url]
          gd stop | status [--endpoint url]
          gd capture --out frame.png [--endpoint url]
          gd grammar
        exit: 0 ok · 1 validation/usage failure · 2 bridge unreachable
        """);

    private static void Grammar() => Console.WriteLine("""
        GameDirector DSL v0.1 — cue types (JSON, times in seconds)
          camera.shot      { t, shot:{ type: lockoff|dolly|orbit|tracking|crane, subject, frame: extreme-closeup|closeup|medium|full|wide, from, to, durationSeconds, fov?, ease: linear|in|out|inOut, lookAt?, params? } }
          actor.spawn      { t, role, location, headingTo? }
          actor.despawn    { t, role }
          actor.anim       { t, role, clip, fade? }
          actor.move       { t, role, to, speed?, headingTo? }
          actor.face       { t, role, headingTo }        (role id or location id)
          audio.play       { t, audioId, volume? 0..1 }
          audio.stop       { t, audioId }
          world.timescale  { t, scale, duration? }       (duration>0 auto-restores to 1.0)
          marker           { t, label }                  (edit beat / review annotation)

        Rules enforced by the compiler:
          - every role/clip/location/audioId/shotType/frameType must exist in the game manifest
          - world.timescale with duration injects an explicit restore cue (visible in compiled output)
          - overlapping camera shots and actor ops on not-yet-spawned roles are warnings, not errors
          - 'current' is a reserved location meaning "wherever the camera is now"
        """);
}
