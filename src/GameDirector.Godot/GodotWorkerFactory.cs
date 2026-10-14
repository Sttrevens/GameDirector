using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

public sealed class GodotWorkerFactory : IGodotWorkerFactory
{
    private readonly string binary, project, stage, source;
    private readonly TextWriter log;
    private readonly string facts;
    public CapabilityManifest Manifest { get; }
    private GodotWorkerFactory(string binary, string project, string stage, TextWriter log, CapabilityManifest manifest, string source, string facts)
    { this.binary = binary; this.project = project; this.stage = stage; this.log = log; Manifest = manifest; this.source = source; this.facts = facts; }
    public static async Task<GodotWorkerFactory> Prepare(string project, string stage, string? binary, TextWriter log, bool headless = false, CancellationToken ct = default)
    {
        project = Path.GetFullPath(project); binary ??= Environment.GetEnvironmentVariable("GAMEDIRECTOR_GODOT") ?? "godot";
        if (!File.Exists(Path.Combine(project, "project.godot"))) throw new ArgumentException("Godot project.godot missing at " + project);
        if (!stage.StartsWith("res://", StringComparison.Ordinal) || !stage.EndsWith(".tscn", StringComparison.Ordinal) || stage.Contains(".."))
            throw new ArgumentException("--stage must be a project-local res://scene.tscn");
        if (!File.Exists(Path.Combine(project, stage[6..]))) throw new ArgumentException("Godot stage missing: " + stage);
        if (!File.Exists(Path.Combine(project, "addons/gamedirector/worker.tscn"))) throw new ArgumentException("Import the gamedirector addon into " + project + "/addons first");
        ProjectPreflight.RequireNoAutoload(project);
        ProjectPreflight.RequireSceneScriptsAbsent(project, stage);
        // One bounded readiness/import phase per supervisor, before any endpoint is published.
        // A source stamp cannot prove that the generated cache is complete or still present.
        // Warm --import rescans and repairs the cache; take workers never invoke the editor.
        using (var import = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            import.CancelAfter(TimeSpan.FromSeconds(BridgeBudgets.PageLoadSeconds));
            await Import(binary, project, log, import.Token);
        }
        using var load = CancellationTokenSource.CreateLinkedTokenSource(ct); load.CancelAfter(TimeSpan.FromSeconds(BridgeBudgets.PageLoadSeconds));
        var source = Source(project);
        await using var worker = await GodotProcess.Open(binary, project, stage, headless, log, load.Token);
        var manifest = DslJson.DeserializeAuthored<CapabilityManifest>((await worker.Inspect(load.Token)).GetRawText());
        var facts = worker.Facts.GetRawText(); var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + facts))).ToLowerInvariant();
        GodotContract.Stamp(manifest, project, fingerprint);
        var validation = GameDirector.Core.Compilation.TimelineCompiler.ValidateManifest(manifest);
        if (validation.HasErrors) throw new InvalidOperationException("Godot manifest: " + string.Join("; ", validation.Diagnostics));
        if (Source(project) != source) throw new BridgeRequestException(BridgeErrors.StateConflict, "Godot sources changed during inspection; in the GameDirector dock click Restart bridge (or restart gd godot serve)");
        return new(binary, project, stage, log, manifest, source, facts);
    }
    public void AssertFresh() { if (Source(project) != source) throw new BridgeRequestException(BridgeErrors.StateConflict, "Godot project/addon sources changed; in the GameDirector dock click Restart bridge (or restart gd godot serve)"); }
    public async Task<IGodotWorker> Open(CancellationToken ct)
    {
        AssertFresh(); using var load = CancellationTokenSource.CreateLinkedTokenSource(ct); load.CancelAfter(TimeSpan.FromSeconds(BridgeBudgets.PageLoadSeconds));
        var worker = await GodotProcess.Open(binary, project, stage, false, log, load.Token);
        if (worker.Facts.GetRawText() == facts) return worker;
        await worker.DisposeAsync(); throw new BridgeRequestException(BridgeErrors.StateConflict, "Godot engine/renderer/driver/device changed; restart the supervisor");
    }
    private static async Task Import(string binary, string project, TextWriter log, CancellationToken ct)
    {
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // Import does not render text. Avoid headless editor UI's native font shaping, which
        // can stall TextServerAdvanced/HarfBuzz before exit; never change the take renderer.
        foreach (var arg in new[] { "--headless", "--path", project, "--editor", "--import", "--text-driver", "Dummy" }) info.ArgumentList.Add(arg);
        var elapsed = Stopwatch.StartNew();
        await log.WriteLineAsync("[Godot] startup import/readiness begin");
        using var p = Process.Start(info) ?? throw new InvalidOperationException("could not import Godot project");
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(ct); }
        catch { if (!p.HasExited) p.Kill(true); await p.WaitForExitAsync(); await log.WriteLineAsync(await output); await log.WriteLineAsync(await error); throw; }
        await log.WriteLineAsync(await output);
        var errors = await error; if (errors.Length > 0) await log.WriteLineAsync(errors);
        if (p.ExitCode != 0) throw new InvalidOperationException("Godot import failed, exit " + p.ExitCode);
        await log.WriteLineAsync($"[Godot] startup import/readiness complete ({elapsed.Elapsed.TotalSeconds:F3}s)");
    }
    private static string Source(string project)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Collect(string folder, HashSet<string> ancestors) {
            var real = new DirectoryInfo(folder).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(folder);
            if (!ancestors.Add(real)) throw new ArgumentException("cyclic source directory link: " + folder);
            foreach (var p in Directory.EnumerateFileSystemEntries(folder).OrderBy(p => p, StringComparer.Ordinal)) {
                var name = Path.GetFileName(p);
                if (name is ".godot" or ".git" or "bin" or "obj" || name.EndsWith(".import", StringComparison.Ordinal)) continue;
                if (Directory.Exists(p)) Collect(p, ancestors);
                else { hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(project, p).Replace('\\', '/'))); hash.AppendData(new byte[] { 0 }); hash.AppendData(File.ReadAllBytes(p)); }
            }
            ancestors.Remove(real);
        }
        Collect(project, new(StringComparer.Ordinal));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
