using System.Diagnostics;
using System.Text.Json;

namespace GameDirector.Client;

/// <summary>One way to obtain the complete media tool unit on this machine.
/// Data comes from the declarative media-distributions manifest; this engine
/// only executes. Concurrent installs are serialized process-wide, every
/// strategy ends with a full re-verification of encoding + ffprobe, and a
/// receipt (success or failure) is always written. Nothing here runs without
/// an explicit local user action, and nothing alters system config, PATH,
/// game scenes or credentials.</summary>
public static class MediaInstaller
{
    public sealed record Option(string Id, string Kind, string Label, string Detail, string License);

    public sealed record Receipt(
        string OptionId, string StartedUtc, bool Installed, string? Note,
        string[] Output, object? Tools, string? Error, string? ReceiptPath = null)
    {
        public string? NoteCode { get; init; }
    }

    /// <summary>Serialize installs: two overlapping installs would race the
    /// same user media folder and the resolution cache.</summary>
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    /// <summary>Options for the current platform. Incomplete strategies are
    /// already impossible (the manifest refuses them at parse time).</summary>
    public static IReadOnlyList<Option> Options(MediaDistributions manifest, string rid)
    {
        var options = new List<Option>();
        foreach (var s in manifest.StrategiesFor(rid).Where(s => s.Kind == "packageManager"))
            if (FindManager(s) != null)
                options.Add(new(s.Id, s.Kind, s.Label, s.Detail, s.License));
        foreach (var d in manifest.DownloadsFor(rid))
            options.Add(new(d.Id, "download", d.Label, d.Detail + " (installed into your GameDirector media folder after pinned SHA-256 verification)", d.License));
        var bundle = manifest.UserBundle;
        options.Add(new(bundle.Id, "userBundle", bundle.Label, bundle.Detail, bundle.License));
        return options;
    }

    /// <summary>Package-manager executable: PATH first (probe run), then the
    /// declared well-known install locations.</summary>
    public static string? FindManager(MediaDistributions.Strategy strategy)
    {
        if (Runnable(strategy.Manager, "--version")) return strategy.Manager;
        return MediaDistributions.ManagerCandidatePaths(strategy)
            .Where(File.Exists)
            .FirstOrDefault(p => Runnable(p, "--version"));
    }

    private static bool Runnable(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return false;
            var drainOut = p.StandardOutput.ReadToEndAsync();
            var drainErr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return false; }
            Task.WaitAll(new Task[] { drainOut, drainErr }, 5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>True when the full encoding contract (ffmpeg encoder + filters,
    /// ffprobe, pinned subtitle font) already holds.</summary>
    public static async Task<bool> Satisfied(CancellationToken ct)
    {
        try { await MediaTools.EnsureEncoding(MediaProfile.Default, true, ct); return true; }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Run one offered strategy by id, then re-verify the full encoding
    /// contract and persist a receipt. Idempotent: a satisfied contract is a no-op.
    /// descriptorPath is required for the user-bundle option.</summary>
    public static async Task<Receipt> Install(MediaDistributions manifest, string rid, string optionId,
        string? descriptorPath, string receiptFolder, CancellationToken ct, Action<string>? progress = null)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            // The per-user media folder is shared by the CLI and the Workbench,
            // which are separate processes: serialize them with an OS-level file
            // lock (released by the OS even if a holder dies — no stale locks).
            await using var folderLock = await AcquireFolderLock(MediaTools.UserMediaFolder, ct);
            return await InstallLocked(manifest, rid, optionId, descriptorPath, receiptFolder, ct, progress);
        }
        finally { InstallGate.Release(); }
    }

    /// <summary>Cross-process install lock: an exclusively held file inside the
    /// shared media folder. Retries until acquired or cancelled.</summary>
    internal static async Task<FileStream> AcquireFolderLock(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var lockPath = Path.Combine(folder, ".install.lock");
        var waiting = Stopwatch.StartNew();
        var maximumWait = TimeSpan.FromSeconds(30);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                if (waiting.Elapsed >= maximumWait)
                    throw new StateConflictException("Could not acquire the media installation lock within " + maximumWait.TotalSeconds + " seconds. Check another running installer and the media folder's availability.", retryable:true, inner:ex);
                await Task.Delay(250, ct);
            }
        }
    }

    private static async Task<Receipt> InstallLocked(MediaDistributions manifest, string rid, string optionId,
        string? descriptorPath, string receiptFolder, CancellationToken ct, Action<string>? progress)
    {
        var started = DateTime.UtcNow;
        var output = new List<string>();
        void Log(string text) { lock (output) output.Add(text); progress?.Invoke(text); }
        var receiptPath = Path.Combine(receiptFolder,
            "media-install-" + started.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".json");

        async Task<Receipt> Finish(bool installed, string? note, string? error, string? noteCode = null)
        {
            var tools = MediaTools.Resolutions().Select(r => new { r.Name, r.Path, r.Source, r.Broken }).ToArray();
            string[] lines; lock (output) lines = output.ToArray();
            var receipt = new Receipt(optionId, started.ToString("o"), installed, note, lines, tools, error, receiptPath) { NoteCode = noteCode };
            try
            {
                Directory.CreateDirectory(receiptFolder);
                // The receipt matters most when things go wrong; even a cancelled
                // install leaves its output behind for diagnosis.
                await File.WriteAllTextAsync(receiptPath,
                    JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n", CancellationToken.None);
            }
            catch (IOException) { /* the install result stands on its own */ }
            return receipt;
        }

        if (await Satisfied(ct))
            return await Finish(false, "Encoding tools are already available. Nothing was installed.", null, "media_already_ready");

        var option = Options(manifest, rid).FirstOrDefault(o => o.Id == optionId)
            ?? throw new ArgumentException($"This installation method is unavailable on this platform: {optionId}");

        string? error = null;
        try
        {
            switch (option.Kind)
            {
                case "packageManager":
                {
                    var strategy = manifest.StrategiesFor(rid).First(s => s.Id == optionId);
                    var manager = FindManager(strategy)
                        ?? throw new MediaFailureException($"Package manager {strategy.Manager} was not found. This option should not have been offered; choose another installation method.");
                    await RunLogged(manager, strategy.Args, TimeSpan.FromSeconds(strategy.TimeoutSeconds), Log, ct);
                    break;
                }
                case "download":
                {
                    var download = manifest.DownloadsFor(rid).First(d => d.Id == optionId);
                    var tempFolder = Path.Combine(Path.GetTempPath(), "gd-media-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(tempFolder);
                        var archiveName = Path.GetFileName(new Uri(download.Url).LocalPath);
                        var local = Path.Combine(tempFolder, archiveName);
                        Log("download " + download.Url);
                        await PinnedDownload.Fetch(new Uri(download.Url), download.ArchiveSha256, download.MaxBytes, local, ct);
                        var descriptor = new MediaBundleDescriptor
                        {
                            SchemaVersion = 1, Rid = rid,
                            Archive = archiveName, ArchiveSha256 = download.ArchiveSha256,
                            Files = download.Files, License = download.License, Source = download.Source,
                        };
                        await MediaBundleInstaller.InstallDescriptor(descriptor, tempFolder, manifest.Unit,
                            MediaTools.UserMediaFolder, Log, ct);
                    }
                    finally { try { Directory.Delete(tempFolder, true); } catch (IOException) { } }
                    break;
                }
                case "userBundle":
                {
                    if (string.IsNullOrWhiteSpace(descriptorPath))
                        throw new ArgumentException("user-bundle requires a bundle descriptor JSON path.");
                    await MediaBundleInstaller.Install(descriptorPath, rid, manifest.Unit,
                        MediaTools.UserMediaFolder, Log, ct);
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown media install kind '{option.Kind}'");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The receipt promise holds on cancellation too: whatever output
            // the strategy produced stays behind for diagnosis.
            await Finish(false, "Installation cancelled.", "cancelled", "media_install_cancelled");
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            await Finish(false, null, error);
            if (ex is StoreIntegrityException) throw new StoreIntegrityException("Installation did not complete: " + ex.Message + ". Full output is saved in " + receiptPath, ex);
            throw new MediaFailureException("Installation did not complete: " + ex.Message + ". Full output is saved in " + receiptPath, ex);
        }

        MediaTools.InvalidateCache();
        try
        {
            // Whatever remains missing throws here, honestly: a strategy is only
            // "ready" when the complete ffmpeg+ffprobe encoding contract holds.
            await MediaTools.EnsureEncoding(MediaProfile.Default, true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Finish(false, "Post-install verification cancelled.", "cancelled", "media_verification_cancelled");
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            error = ex.Message;
            await Finish(false, null, error);
            throw new MediaFailureException("The complete encoding check still fails after installation: " + ex.Message + ". Full output is saved in " + receiptPath, ex);
        }
        return await Finish(true, null, null);
    }

    /// <summary>Run a package manager with a hard timeout, draining both output
    /// streams in every outcome — success, timeout kill and user cancellation —
    /// so the receipt always carries what the manager printed.</summary>
    private static async Task RunLogged(string exe, string[] args, TimeSpan timeout, Action<string> output, CancellationToken ct)
    {
        output("> " + exe + " " + string.Join(" ", args));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new MediaFailureException("could not start " + exe);
        var so = ReadLines(p.StandardOutput, output);
        var se = ReadLines(p.StandardError, output);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using (linked.Token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }))
        {
            var timedOut = false;
            try
            {
                try { await p.WaitForExitAsync(linked.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
            }
            finally
            {
                // Bounded drain: a killed tree normally closes both pipes at
                // once, but a surviving pipe holder must never hang the receipt.
                await Drain(so, output); await Drain(se, output);
            }
            if (timedOut)
                throw new TimeoutException($"{exe} did not finish within {timeout.TotalMinutes:0} minutes and was terminated.");
            if (p.ExitCode != 0)
                throw new MediaFailureException(exe + " exited " + p.ExitCode + "; see the installation output/receipt.");
        }
    }

    private static async Task ReadLines(StreamReader stream, Action<string> output)
    {
        while (await stream.ReadLineAsync() is { } line) output(line);
    }
    private static async Task Drain(Task stream, Action<string> output)
    {
        try
        {
            var done = await Task.WhenAny(stream, Task.Delay(TimeSpan.FromSeconds(15)));
            if (done == stream) await stream;
            else output("(output pipe stayed open after termination; truncated)");
        }
        catch (Exception) { output("(manager output unreadable)"); }
    }
}
