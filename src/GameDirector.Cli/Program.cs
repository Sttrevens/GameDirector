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

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return 0; }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        string command = args[0];
        string? positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string endpoint = Option(args, "--endpoint") ?? GameDirector.Core.Dsl.BridgeDefaults.UnityEndpoint;
        string? manifestPath = Option(args, "--manifest");
        string? outPath = Option(args, "--out");

        try
        {
            switch (command)
            {
                case "guide": Console.WriteLine(DirectorProduct.Guide()); return 0;
                case "doctor": {
                    var report=await DirectorProduct.Doctor(Option(args,"--endpoint"),cancellation.Token);
                    var json=DslJson.Serialize(report);Console.WriteLine(json);
                    return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean()?0:1;
                }
                case "catalog-init": {
                    if(manifestPath==null || outPath==null)return Fail("catalog-init requires --manifest and --out");
                    using var file=new FileStream(outPath,FileMode.CreateNew,FileAccess.Write);
                    using var writer=new StreamWriter(file);writer.Write(DslJson.Serialize(CatalogTools.Scaffold(manifestPath)));return 0;
                }
                case "catalog-check": {
                    if(positional==null || manifestPath==null)return Fail("catalog-check requires catalog path and --manifest");
                    Console.WriteLine(DslJson.Serialize(CatalogTools.Inspect(positional,manifestPath)));return 0;
                }
                case "resume": {
                    if(positional==null)return Fail("resume requires a take directory");
                    Console.WriteLine(await TakeRecorder.Resume(positional,Console.Error.WriteLine,cancellation.Token));return 0;
                }
                case "compile":
                case "validate":
                {
                    if (positional == null) return Fail("validate requires a timeline path");
                    var timeline = DslJson.Load<TimelineAsset>(positional);
                    var manifest = manifestPath != null
                        ? DslJson.Load<CapabilityManifest>(manifestPath)
                        : await new DirectorBridgeClient(endpoint).GetManifestAsync();
                    var result = TimelineCompiler.Compile(timeline, manifest);
                    foreach (var d in result.Diagnostics) { if(command=="compile")Console.Error.WriteLine(d.ToString());else Console.WriteLine(d.ToString()); }
                    if (result.HasErrors) { Console.Error.WriteLine($"INVALID: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)"); return 1; }
                    if(command=="compile" && result.Diagnostics.Any(d=>d.Code==TimelineCompiler.WRoleNotPresent)) return Fail("compile requires roles to be present when used");
                    if(command=="compile"){Console.WriteLine(DslJson.Serialize(result.Timeline));return 0;}
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

                case "take":
                {
                    if(positional==null || outPath==null)return Fail("take requires timeline and --out <new directory>");
                    Console.WriteLine(await TakeRecorder.Record(positional,outPath,endpoint,
                        int.Parse(Option(args,"--fps")??"24"),int.Parse(Option(args,"--width")??"1280"),int.Parse(Option(args,"--height")??"720"),Console.Error.WriteLine,cancellation.Token));
                    return 0;
                }
                case "edit":
                {
                    if(positional==null || outPath==null)return Fail("edit requires edit.json and --out <new directory>");
                    Console.WriteLine(await EditRenderer.Render(positional,outPath,cancellation.Token));return 0;
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
          gd take <timeline.json> --out <new directory> [--fps 24 --width 1280 --height 720 --endpoint url]
          gd edit <edit.json> --out <new directory>
          gd grammar | guide
          gd doctor [--endpoint url]
          gd compile <timeline.json> --manifest m.json
          gd catalog-init --manifest m.json --out performances.json
          gd catalog-check performances.json --manifest m.json
          gd resume <take-directory>
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
