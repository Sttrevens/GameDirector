using System.Globalization;
using System.Text;
using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Cli;

/// <summary>
/// gd — GameDirector command line. One CommandSpec table drives both parsing
/// and usage: an option that is not in the table is an error, never silently
/// ignored (a misspelled --frame-rate must fail, not capture at 24 fps).
/// Results go to stdout; usage and errors go to stderr.
///
/// Exit codes: 0 ok · 1 operation failed (invalid timeline, admission failure,
/// engine/bridge error) · 2 usage error (unknown command/option, missing value).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        return await Run(args, Console.Out, Console.Error, cancellation.Token);
    }

    /// <summary>In-process entry point, so the CLI's parsing, exit codes and
    /// stream routing are pinned by contract tests instead of black-box scripts.</summary>
    public static async Task<int> Run(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { stdout.Write(Usage()); return 0; }
        var spec = Array.Find(Commands, c => c.Name == args[0]);
        if (spec == null) return UsageError(stderr, $"unknown command '{args[0]}'");
        var invocation = new Invocation { Spec = spec, Args = args, Stdout = stdout, Stderr = stderr, Ct = ct };
        if (!spec.PassThrough)
        {
            if (args.Skip(1).Any(a => a is "-h" or "--help")) { stdout.Write(Usage(spec)); return 0; }
            var error = Parse(spec, args, invocation);
            if (error != null) return UsageError(stderr, error, spec);
        }
        try { return await spec.Body(invocation); }
        catch (Exception ex) { stderr.WriteLine(ex.Message); return 1; }
    }

    // ---- the table: parser and usage both derive from these specs ----

    private sealed record OptionSpec(string Name, string ValueName = "", string? Default = null, bool Required = false, bool Integer = false, bool Flag = false);
    private sealed record CommandSpec(string Name, string Summary, string? Positional, OptionSpec[] Options, bool PassThrough, Func<Invocation, Task<int>> Body);

    private static async Task<int> GodotCommand(Invocation inv)
    {
        if (inv.Positional is not ("serve" or "manifest")) return UsageError(inv.Stderr, "godot requires serve or manifest", inv.Spec);
        if (inv.Int("--port") < 0 || inv.Int("--port") > ushort.MaxValue) return UsageError(inv.Stderr, "--port must be 0..65535 (0 selects an ephemeral port)", inv.Spec);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(inv.Ct);
        using var signal = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        var logPath = inv.Option(GameDirector.Godot.GodotDockOwner.LogOption);
        using var dockLog = logPath == null ? null : GameDirector.Godot.GodotDockOwner.OpenLog(logPath);
        TextWriter log = dockLog ?? TextWriter.Synchronized(inv.Stderr);
        TextWriter output = dockLog ?? inv.Stdout;
        var token = Environment.GetEnvironmentVariable(GameDirector.Godot.GodotDockOwner.TokenEnvironment);
        Environment.SetEnvironmentVariable(GameDirector.Godot.GodotDockOwner.TokenEnvironment, null);
        try {
            var factory = await GameDirector.Godot.GodotWorkerFactory.Prepare(inv.Option("--project")!, inv.Option("--stage")!, inv.Option("--godot"),
                log, headless: inv.Positional == "manifest", ct: stop.Token);
            if (inv.Positional == "manifest") { output.WriteLine(DslJson.Serialize(factory.Manifest)); return 0; }
            var owner = string.IsNullOrEmpty(token) ? null : new GameDirector.Godot.GodotDockOwner(token, Path.GetFullPath(inv.Option("--project")!));
            await using var bridge = await GameDirector.Godot.GodotBridge.Start(factory, inv.Int("--port"), stop.Token, owner);
            output.WriteLine(DslJson.Serialize(new { endpoint = bridge.Endpoint, game = factory.Manifest.Game, engine = GameDirector.Godot.GodotDefaults.Engine }));
            output.Flush();
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            return 0;
        } catch (Exception ex) { if (dockLog != null) dockLog.WriteLine(ex.Message); throw; }
    }

    private static OptionSpec Opt(string name, string value, bool required = false) => new(name, value, Required: required);
    private static OptionSpec IntOpt(string name, string value, int @default) => new(name, value, Default: @default.ToString(CultureInfo.InvariantCulture), Integer: true);
    private static OptionSpec Flag(string name, bool required = false) => new(name, Flag: true, Required: required);
    private static readonly OptionSpec[] None = Array.Empty<OptionSpec>();
    // One --take envelope for every verdict — success, compile/admission
    // refusal, strict parse refusal of the caller's timeline — so engines parse
    // exactly one document shape and never guess from shape absence.
    private static object TakeEnvelope(int frameCount, CompiledTimeline? timeline, IReadOnlyList<string> errors) =>
        new { contract = EngineContract.Current.Version.ToString(), id = timeline?.Id, title = timeline?.Title, orderedCues = timeline?.OrderedCues, duration = timeline?.Duration,
            frameCount = timeline == null ? 0 : frameCount, admission = new { ok = timeline != null && errors.Count == 0, errors } };
    private static readonly OptionSpec[] EndpointOnly = { new OptionSpec("--endpoint", "url") };

    private sealed class Invocation
    {
        public required CommandSpec Spec { get; init; }
        public required string[] Args { get; init; }
        public required TextWriter Stdout { get; init; }
        public required TextWriter Stderr { get; init; }
        public required CancellationToken Ct { get; init; }
        public string? Positional { get; set; }
        public Dictionary<string, string?> Values { get; } = new();

        public string? Option(string name) =>
            Values.TryGetValue(name, out var value) ? value : Spec.Options.FirstOrDefault(o => o.Name == name)?.Default;
        public int Int(string name) => int.Parse(Option(name)!, CultureInfo.InvariantCulture);
        public bool Flag(string name) => Values.ContainsKey(name);
        public string Endpoint => Option("--endpoint") ?? BridgeDefaults.UnityEndpoint;
    }

    private static readonly CommandSpec[] Commands =
    {
        new("validate", "compile-check a timeline against a manifest or the live game", "timeline.json",
            new[] { Opt("--manifest", "m.json"), Opt("--endpoint", "url"),
                IntOpt("--width", "W", FilmDefaults.Width), IntOpt("--height", "H", FilmDefaults.Height) }, false, async inv =>
            {
                var result = await DirectorOperations.ValidateTimeline(inv.Positional!, inv.Option("--manifest"), inv.Option("--endpoint"), inv.Ct,
                    inv.Flag("--width") ? inv.Int("--width") : null, inv.Flag("--height") ? inv.Int("--height") : null);
                foreach (var d in result.Diagnostics) inv.Stdout.WriteLine(d.ToString());
                if (result.HasErrors) { inv.Stderr.WriteLine($"INVALID: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)"); return 1; }
                inv.Stdout.WriteLine($"VALID: '{result.Timeline!.Id}' — {result.Timeline.OrderedCues.Count} cues, duration {result.Timeline.Duration:0.###}s");
                return 0;
            }),
        new("manifest", "print the live game manifest", null, EndpointOnly, false, async inv =>
            {
                inv.Stdout.WriteLine(DslJson.Serialize(await DirectorOperations.WithReachableBridge(inv.Option("--endpoint"), c => c.GetManifestAsync(inv.Ct), inv.Ct)));
                return 0;
            }),
        new("play", "play a timeline in the running game (director mode)", "timeline.json", EndpointOnly, false, async inv =>
            {
                var timeline = AspectHints.Resolve(DslJson.LoadAuthored<TimelineAsset>(inv.Positional!));
                inv.Stdout.WriteLine(await DirectorOperations.WithReachableBridge(inv.Option("--endpoint"), c => c.PlayAsync(timeline, inv.Ct), inv.Ct));
                return 0;
            }),
        new("stop", "stop the currently playing timeline", null, EndpointOnly, false, async inv =>
            {
                await DirectorOperations.WithReachableBridge(inv.Option("--endpoint"), c => c.StopAsync(inv.Ct), inv.Ct);
                inv.Stdout.WriteLine("stopped");
                return 0;
            }),
        new("status", "print playback status from the running game", null, EndpointOnly, false, async inv =>
            {
                inv.Stdout.WriteLine(DslJson.Serialize(await DirectorOperations.WithReachableBridge(inv.Option("--endpoint"), c => c.GetStatusAsync(inv.Ct), inv.Ct)));
                return 0;
            }),
        new("capture", "capture the current director camera frame", null,
            new[] { Opt("--out", "frame.png", required: true), Opt("--endpoint", "url") }, false, async inv =>
            {
                await DirectorOperations.WithReachableBridge(inv.Option("--endpoint"), async c => {
                    var path = OutputPolicy.RequireOutput(inv.Option("--out")!, OutputPolicy.ManifestRoot(await c.GetManifestAsync(inv.Ct)));
                    var bytes = await c.CaptureFrameAsync(inv.Ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes, inv.Ct);
                }, inv.Ct);
                inv.Stdout.WriteLine("wrote " + inv.Option("--out"));
                return 0;
            }),
        new("take", "record a frame-stepped take into a new directory", "timeline.json",
            new[] { Opt("--out", "dir", required: true), Opt("--endpoint", "url"),
                IntOpt("--fps", "N", FilmDefaults.FrameRate), IntOpt("--width", "W", FilmDefaults.Width), IntOpt("--height", "H", FilmDefaults.Height),
                Flag("--keep-frames"), IntOpt("--from-frame", "N", 0) }, false, async inv =>
            {
                inv.Stdout.WriteLine(await TakeRecorder.Record(inv.Positional!, inv.Option("--out")!, inv.Endpoint,
                    inv.Int("--fps"), inv.Int("--width"), inv.Int("--height"), inv.Stderr.WriteLine, inv.Ct,
                    retainFrames: inv.Flag("--keep-frames"), captureFromFrame: inv.Int("--from-frame")));
                return 0;
            }),
        new("expect-record", "record a measured regression sidecar (not an aesthetic check)", "timeline.json",
            new[] { Opt("--out", "file.expect.json", required: true), Opt("--endpoint", "url", required: true), IntOpt("--fps", "N", FilmDefaults.FrameRate),
                Opt("--sizes", "WxH,WxH"), Opt("--beats", "label=frame,..."), Opt("--work", "new-dir") }, false, async inv =>
            {
                ExpectRunResult result;
                try { result=await ExpectRecorder.Record(inv.Positional!,inv.Option("--out")!,inv.Endpoint,inv.Int("--fps"),inv.Option("--sizes"),inv.Option("--beats"),inv.Option("--work"),inv.Ct,inv.Stderr.WriteLine); }
                catch(Exception ex)when(ex is ArgumentException or AuthoredInputException){return UsageError(inv.Stderr,ex.Message,inv.Spec);}
                inv.Stdout.WriteLine(result.Measured?DslJson.Serialize(result.File):DslJson.Serialize(new{measured=false,note=result.Note}));return result.Measured?0:1;
            }),
        new("expect-check", "check measured regression metrics against a sidecar", "timeline.json",
            new[] { Opt("--expect", "file", required: true), Opt("--endpoint", "url", required: true), Opt("--work", "new-dir") }, false, async inv =>
            {
                ExpectRunResult result;
                try { result=await ExpectRecorder.Check(inv.Positional!,inv.Option("--expect")!,inv.Endpoint,inv.Option("--work"),inv.Ct,inv.Stderr.WriteLine); }
                catch(Exception ex)when(ex is ArgumentException or AuthoredInputException){return UsageError(inv.Stderr,ex.Message,inv.Spec);}
                inv.Stdout.WriteLine(result.Measured?DslJson.Serialize(result.Check):DslJson.Serialize(new{measured=false,note=result.Note}));return result.Measured&&result.Check!.Passed?0:1;
            }),
        new("measure", "measure exact rendered timeline frames into a new directory", "timeline.json",
            new[] { Opt("--frames", "N,A-B,...", required: true), Opt("--out", "dir", required: true), Opt("--endpoint", "url"),
                IntOpt("--fps", "N", FilmDefaults.FrameRate), IntOpt("--width", "W", FilmDefaults.Width), IntOpt("--height", "H", FilmDefaults.Height) }, false, async inv =>
            {
                MeasureRecordResult result;
                try { result=await MeasureRecorder.Record(inv.Positional!,inv.Option("--out")!,inv.Endpoint,inv.Option("--frames")!,inv.Int("--fps"),inv.Int("--width"),inv.Int("--height"),inv.Ct,inv.Stderr.WriteLine); }
                catch(FrameSelectionException ex) { return UsageError(inv.Stderr,ex.Message,inv.Spec); }
                inv.Stdout.WriteLine(result.Measured?DslJson.Serialize(result.Report):DslJson.Serialize(new{measured=false,note=result.Note}));
                return result.Measured?0:1;
            }),
        new("edit", "render an edit decision list into a new directory", "edit.json",
            new[] { Opt("--out", "dir", required: true) }, false, async inv =>
            {
                inv.Stdout.WriteLine(await EditRenderer.Render(inv.Positional!, inv.Option("--out")!, inv.Ct));
                return 0;
            }),
        new("grammar", "print the DSL quick reference (generated from Core)", null, None, false, inv =>
            {
                inv.Stdout.Write(DirectorProduct.Grammar());
                return Task.FromResult(0);
            }),
        new("guide", "print the complete director workflow guide", null, None, false, inv =>
            {
                inv.Stdout.WriteLine(DirectorProduct.Guide());
                return Task.FromResult(0);
            }),
        new("doctor", "check media tooling and optionally the live game bridge", null, EndpointOnly, false, async inv =>
            {
                var report = await DirectorProduct.Doctor(inv.Option("--endpoint"), inv.Ct);
                var json = DslJson.Serialize(report);
                inv.Stdout.WriteLine(json);
                return JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
            }),
        new("compile", "print the compiled timeline; --take adds the engine admission verdict", "timeline.json",
            new[] { Opt("--manifest", "m.json"), Opt("--endpoint", "url"), Flag("--take"),
                IntOpt("--fps", "N", FilmDefaults.FrameRate), IntOpt("--width", "W", FilmDefaults.Width), IntOpt("--height", "H", FilmDefaults.Height), IntOpt("--from-frame", "N", 0) }, false, async inv =>
            {
                if (inv.Flag("--take"))
                {
                    // The take gate an engine would apply, exported for
                    // non-.NET runtimes: compiled timeline + frameCount +
                    // admission verdict in one JSON document.
                    GameDirector.Core.Capture.AdmissionResult admission;
                    try { admission = await DirectorOperations.AdmitTake(inv.Positional!, inv.Option("--manifest"), inv.Option("--endpoint"),
                        inv.Int("--fps"), inv.Int("--width"), inv.Int("--height"), inv.Ct, inv.Int("--from-frame"), inv.Flag("--width") && inv.Flag("--height")); }
                    catch(AuthoredInputException ex) when (ex.Document==AuthoredDocumentKind.Timeline) {
                        // Only the caller's timeline failing strict parse is an
                        // admission verdict. A manifest failure is engine/adapter
                        // skew: it exits below with no envelope so engines map it
                        // to an internal fault instead of blaming the film.
                        inv.Stdout.WriteLine(DslJson.Serialize(TakeEnvelope(0,null,new[]{ex.Message})));
                        inv.Stderr.WriteLine(ex.Message);return 1;
                    }
                    foreach (var d in admission.Compiled.Diagnostics) inv.Stderr.WriteLine(d.ToString());
                    inv.Stdout.WriteLine(DslJson.Serialize(TakeEnvelope(
                        admission.Compiled.Timeline==null?0:admission.FrameCount, admission.Compiled.Timeline, admission.Errors)));
                    if (!admission.Ok) { inv.Stderr.WriteLine($"INVALID: {admission.Errors.Count} error(s)"); return 1; }
                    return 0;
                }
                var result = await DirectorOperations.ValidateTimeline(inv.Positional!, inv.Option("--manifest"), inv.Option("--endpoint"), inv.Ct,
                    inv.Flag("--width") ? inv.Int("--width") : null, inv.Flag("--height") ? inv.Int("--height") : null);
                foreach (var d in result.Diagnostics) inv.Stderr.WriteLine(d.ToString());
                if (result.HasErrors) { inv.Stderr.WriteLine($"INVALID: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)"); return 1; }
                inv.Stdout.WriteLine(DslJson.Serialize(result.Timeline));
                return 0;
            }),
        new("contract", "engine-facing capture/vocabulary constants", null,
            new[] { Flag("--json", required: true) }, false, inv =>
            {
                inv.Stdout.WriteLine(DslJson.Serialize(EngineContract.Current));
                return Task.FromResult(0);
            }),
        new("catalog-init", "scaffold a performance catalog from a manifest", null,
            new[] { Opt("--manifest", "m.json", required: true), Opt("--out", "performances.json", required: true) }, false, inv =>
            {
                using var file = new FileStream(inv.Option("--out")!, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(file);
                writer.Write(DslJson.Serialize(CatalogTools.Scaffold(inv.Option("--manifest")!)));
                return Task.FromResult(0);
            }),
        new("catalog-check", "inspect a performance catalog against its manifest", "performances.json",
            new[] { Opt("--manifest", "m.json", required: true) }, false, inv =>
            {
                inv.Stdout.WriteLine(DslJson.Serialize(CatalogTools.Inspect(inv.Positional!, inv.Option("--manifest")!)));
                return Task.FromResult(0);
            }),
        new("catalog-restamp", "re-stamp a legacy catalog with the current manifest identity", "performances.json",
            new[] { Opt("--manifest", "m.json", required: true), Flag("--force") }, false, inv =>
            {
                var errors = CatalogTools.Restamp(inv.Positional!, inv.Option("--manifest")!, inv.Flag("--force"));
                inv.Stdout.WriteLine(DslJson.Serialize(new { status = errors.Count == 0 ? "Valid" : "Invalid", errors }));
                return Task.FromResult(errors.Count == 0 ? 0 : 1);
            }),
        new("review", "review the latest Verified take's composition (JSON; warnings never fail)", "take-directory",
            new[] { Opt("--safe-area", "preset|l,r,t,b") }, false, inv =>
            {
                SafeArea? safe=null;
                if(inv.Option("--safe-area") is string area&&!SafeArea.Presets.TryGetValue(area,out safe)) {
                    var names=string.Join(", ",SafeArea.Presets.Keys);if(!area.Contains(','))return Task.FromResult(UsageError(inv.Stderr,"unknown --safe-area preset '"+area+"'; known presets: "+names,inv.Spec));
                    var values=area.Split(',');var insets=new double[4];
                    if(values.Length!=4||values.Where((v,i)=>!double.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out insets[i])).Any())
                        return Task.FromResult(UsageError(inv.Stderr,"--safe-area requires a preset ("+names+") or numeric l,r,t,b insets",inv.Spec));
                    try {safe=new SafeArea(insets[0],insets[1],insets[2],insets[3]);}
                    catch(ArgumentException ex){return Task.FromResult(UsageError(inv.Stderr,"--safe-area: "+ex.Message,inv.Spec));}
                }
                var report=TakeReview.Read(inv.Positional!,safe);inv.Stdout.WriteLine(DslJson.Serialize(report));
                return Task.FromResult(report.Judged&&report.Review!.Errors==0?0:1);
            }),
        new("resume", "recover a persisted take (re-encode or replay into a new attempt)", "take-directory", None, false, async inv =>
            {
                inv.Stdout.WriteLine((await TakeRecorder.Resume(inv.Positional!, inv.Stderr.WriteLine, inv.Ct)).Video);
                return 0;
            }),
        new("workbench", "open the local Workbench with --open, or launch the bundled app (args pass through)", null, None, true, OpenWorkbench),
        new("mcp", "launch the bundled MCP stdio server", null, None, true,
            inv => RuntimeLauncher.RunSibling("mcp", inv.Args.Skip(1), inv.Ct)),
        new("godot", "serve a Godot bridge or export the scene manifest (GDScript addon)", "serve|manifest",
            new[] { Opt("--project", "dir", required: true), Opt("--stage", "res://scene.tscn", required: true), Opt("--godot", "binary"),
                IntOpt("--port", "N", GameDirector.Godot.GodotDefaults.Port),
                Opt(GameDirector.Godot.GodotDockOwner.LogOption, "file") }, false, GodotCommand),
        new("unity", "install the Unity package (plan-only unless --apply)", "install",
            new[] { Opt("--project", "dir", required: true), Opt("--source", "package"), Flag("--apply") }, false, inv =>
            {
                if (inv.Positional != "install") return Task.FromResult(UsageError(inv.Stderr, "unity requires a subcommand: install", inv.Spec));
                var plan = UnityPackageInstaller.Install(inv.Option("--source") ?? UnityPackageInstaller.DefaultSource(), inv.Option("--project")!, inv.Flag("--apply"));
                inv.Stdout.WriteLine(DslJson.Serialize(plan));
                if (plan.Mode == "plan")
                    inv.Stderr.WriteLine("plan only: re-run with --apply to write " + plan.Writes + " file(s)");
                return Task.FromResult(0);
            }),
        new("media", "media tooling: status | install", "status | install",
            new[] { Opt("--option", "id"), Opt("--descriptor", "bundle.json") }, false, async inv =>
            {
                var mediaManifest = MediaDistributions.LoadBundled();
                var rid = DistributionManifest.CurrentRid();
                if (inv.Positional == "status")
                {
                    inv.Stdout.WriteLine(DslJson.Serialize(new
                    {
                        tools = MediaTools.Resolutions(),
                        unit = mediaManifest.Unit.Tools,
                        options = MediaInstaller.Options(mediaManifest, rid),
                        installFolder = MediaTools.UserMediaFolder,
                    }));
                    return 0;
                }
                if (inv.Positional == "install")
                {
                    var optionId = inv.Option("--option");
                    if (optionId == null) return UsageError(inv.Stderr, "media install requires --option <id> (see gd media status)", inv.Spec);
                    var receipt = await MediaInstaller.Install(mediaManifest, rid, optionId, inv.Option("--descriptor"),
                        Path.Combine(MediaTools.UserMediaFolder, "logs"), inv.Ct);
                    inv.Stdout.WriteLine(DslJson.Serialize(receipt));
                    return receipt.Installed || receipt.Note != null ? 0 : 1;
                }
                return UsageError(inv.Stderr, "media requires a subcommand: status | install", inv.Spec);
            }),
    };

    private static async Task<int> OpenWorkbench(Invocation inv)
    {
        // With explicit launch options (--data/--port), preserve pass-through.
        // The bare --open first uses the verified running instance's credential.
        if (inv.Args.Length == 2 && inv.Args[1] == "--open") {
            try {
                var connection = await WorkbenchLocator.ResolveAsync(inv.Ct);
                var url = await connection.BrowserUrl(inv.Ct);
                BrowserLauncher.Open(url, inv.Stdout); return 0;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == null) { /* no running service: launch below */ }
        }
        return await RuntimeLauncher.RunSibling("workbench", inv.Args.Skip(1), inv.Ct);
    }

    // ---- parsing: anything the table does not declare is an error ----

    private static string? Parse(CommandSpec spec, string[] args, Invocation invocation)
    {
        for (int i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var option = Array.Find(spec.Options, o => o.Name == token);
                if (option == null) return $"unknown option '{token}' for 'gd {spec.Name}'";
                if (!invocation.Values.TryAdd(token, null)) return $"duplicate option '{token}'";
                if (option.Flag) continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return $"option '{token}' requires a value";
                var value = args[++i];
                if (option.Integer && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    return $"option '{token}' expects an integer, got '{value}'";
                invocation.Values[token] = value;
            }
            else
            {
                if (spec.Positional == null) return $"unexpected argument '{token}'";
                if (invocation.Positional != null) return $"unexpected argument '{token}' ('gd {spec.Name}' takes one positional)";
                invocation.Positional = token;
            }
        }
        if (spec.Positional != null && invocation.Positional == null) return $"gd {spec.Name} requires <{spec.Positional}>";
        foreach (var option in spec.Options)
            if (option.Required && !invocation.Values.ContainsKey(option.Name))
                return option.Flag ? $"gd {spec.Name} requires {option.Name}" : $"gd {spec.Name} requires {option.Name} <{option.ValueName}>";
        return null;
    }

    private static int UsageError(TextWriter stderr, string message, CommandSpec? spec = null)
    {
        stderr.WriteLine(message);
        stderr.Write(Usage(spec));
        return 2;
    }

    private static string Usage(CommandSpec? only = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("gd — GameDirector CLI");
        foreach (var spec in Commands)
        {
            if (only != null && spec != only) continue;
            sb.Append("  gd ").Append(spec.Name);
            if (spec.PassThrough) sb.Append(" [args...]");
            else
            {
                if (spec.Positional != null) sb.Append(" <").Append(spec.Positional).Append('>');
                foreach (var option in spec.Options)
                {
                    sb.Append(' ');
                    if (!option.Required) sb.Append('[');
                    sb.Append(option.Name);
                    if (!option.Flag) sb.Append(' ').Append(option.Default ?? "<" + option.ValueName + ">");
                    if (!option.Required) sb.Append(']');
                }
            }
            sb.Append("   ").Append(spec.Summary);
            sb.AppendLine();
        }
        sb.AppendLine("exit: 0 ok · 1 operation failed (invalid input, admission failure, engine error) · 2 usage error");
        return sb.ToString();
    }
}
