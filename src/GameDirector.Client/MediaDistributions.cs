using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDirector.Client;

/// <summary>Manifest-driven media tool distributions (media-distributions.json).
/// The install UNIT is the complete capability — ffmpeg and ffprobe together,
/// with the profile's encoder/filters. A strategy or bundle that cannot provide
/// every unit tool is rejected up front and is never advertised as ready.
/// No hashes or URLs are invented: curated downloads stay empty until a complete
/// archive is hash-verified by this repo's contract tests; until then users can
/// supply an offline bundle descriptor whose every file hash is verified.</summary>
public sealed class MediaDistributions
{
    public const string ManifestFileName = "media-distributions.json";

    public int SchemaVersion { get; init; }
    public UnitLayout Unit { get; init; } = new();
    public List<Strategy> Strategies { get; init; } = new();
    public List<Download> Downloads { get; init; } = new();
    public UserBundleLayout UserBundle { get; init; } = new();

    public sealed class UnitLayout
    {
        public string[] Tools { get; init; } = Array.Empty<string>();
        public string[] Encoders { get; init; } = Array.Empty<string>();
        public string[] SubtitleFilters { get; init; } = Array.Empty<string>();
    }

    public sealed class Strategy
    {
        public string Id { get; init; } = "";
        public string Kind { get; init; } = "";
        public string[] Rids { get; init; } = Array.Empty<string>();
        public string Manager { get; init; } = "";
        public string[] ManagerCandidates { get; init; } = Array.Empty<string>();
        public string[] Args { get; init; } = Array.Empty<string>();
        public int TimeoutSeconds { get; init; } = 1800;
        public string[] Provides { get; init; } = Array.Empty<string>();
        public string Label { get; init; } = "";
        public string Detail { get; init; } = "";
        public string License { get; init; } = "";
    }

    /// <summary>A curated pinned download: one archive carrying the complete unit.</summary>
    public sealed class Download
    {
        public string Id { get; init; } = "";
        public string[] Rids { get; init; } = Array.Empty<string>();
        public string Url { get; init; } = "";
        public string ArchiveSha256 { get; init; } = "";
        public long MaxBytes { get; init; } = 300L * 1024 * 1024;
        public Dictionary<string, Artifact> Files { get; init; } = new();
        public string Label { get; init; } = "";
        public string Detail { get; init; } = "";
        public string License { get; init; } = "";
        public string Source { get; init; } = "";
    }

    public sealed class Artifact
    {
        public string Path { get; init; } = "";
        public string Sha256 { get; init; } = "";
    }

    public sealed class UserBundleLayout
    {
        public string Id { get; init; } = "user-bundle";
        public string Label { get; init; } = "";
        public string Detail { get; init; } = "";
        public string License { get; init; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static MediaDistributions Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<MediaDistributions>(json, JsonOptions)
            ?? throw new InvalidDataException("media distribution manifest is empty");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"unsupported media distribution schema {manifest.SchemaVersion}");
        if (manifest.Unit.Tools.Length == 0)
            throw new InvalidDataException("media distribution manifest declares no install unit");
        foreach (var strategy in manifest.Strategies) ValidateComplete(strategy.Id, strategy.Provides, manifest.Unit);
        foreach (var download in manifest.Downloads) ValidateComplete(download.Id, download.Files.Keys, manifest.Unit);
        return manifest;
    }

    /// <summary>Completeness gate: nothing partial ever becomes an option.</summary>
    private static void ValidateComplete(string id, IEnumerable<string> provides, UnitLayout unit)
    {
        var missing = unit.Tools.Where(t => !provides.Contains(t)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"media strategy '{id}' is incomplete (lacks {string.Join(", ", missing)}); incomplete bundles are never advertised or installed");
    }

    public static MediaDistributions Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The copy shipped next to the running app (publish layout).</summary>
    public static MediaDistributions LoadBundled() => Load(Path.Combine(AppContext.BaseDirectory, ManifestFileName));

    public IEnumerable<Strategy> StrategiesFor(string rid) =>
        Strategies.Where(s => s.Rids.Contains(rid));

    public IEnumerable<Download> DownloadsFor(string rid) =>
        Downloads.Where(d => d.Rids.Contains(rid));

    /// <summary>Package-manager executable path: PATH probe first (caller runs
    /// it), then the declared per-platform candidates with env expansion.</summary>
    public static IEnumerable<string> ManagerCandidatePaths(Strategy strategy)
    {
        foreach (var candidate in strategy.ManagerCandidates)
        {
            var expanded = candidate.Replace("%LOCALAPPDATA%",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            yield return Environment.ExpandEnvironmentVariables(expanded);
        }
    }
}

/// <summary>A user-supplied offline bundle descriptor: every tool of the unit,
/// each with a pinned SHA-256, optionally inside one pinned archive.</summary>
public sealed class MediaBundleDescriptor
{
    public int SchemaVersion { get; init; }
    public string? Rid { get; init; }
    public string? Archive { get; init; }
    public string? ArchiveSha256 { get; init; }
    public Dictionary<string, MediaDistributions.Artifact> Files { get; init; } = new();
    public string License { get; init; } = "";
    public string Source { get; init; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static MediaBundleDescriptor Parse(string json, string currentRid, MediaDistributions.UnitLayout unit)
    {
        MediaBundleDescriptor descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<MediaBundleDescriptor>(json, JsonOptions)
                ?? throw new InvalidDataException("empty descriptor");
        }
        catch (JsonException ex) { throw new InvalidDataException($"bundle descriptor is not valid JSON: {ex.Message}"); }
        if (descriptor.SchemaVersion != 1)
            throw new InvalidDataException($"unsupported bundle descriptor schema {descriptor.SchemaVersion}");
        if (descriptor.Rid != null && descriptor.Rid != currentRid)
            throw new InvalidDataException($"bundle descriptor targets '{descriptor.Rid}' but this machine is '{currentRid}'");
        foreach (var tool in unit.Tools)
        {
            if (!descriptor.Files.TryGetValue(tool, out var artifact) || string.IsNullOrWhiteSpace(artifact.Path))
                throw new InvalidDataException($"bundle descriptor is incomplete: it must provide '{tool}'. An ffmpeg-only bundle is not an installable unit.");
            if (!IsSha256(artifact.Sha256))
                throw new InvalidDataException($"bundle descriptor lacks a lowercase SHA-256 for '{tool}'");
            ValidateArchivePath(artifact.Path);
        }
        foreach (var extra in descriptor.Files.Keys.Where(k => !unit.Tools.Contains(k)))
            throw new InvalidDataException($"bundle descriptor provides unknown tool '{extra}'");
        if (descriptor.Archive != null)
        {
            if (!IsSha256(descriptor.ArchiveSha256))
                throw new InvalidDataException("bundle descriptor with an archive must pin archiveSha256");
            ValidateArchivePath(descriptor.Archive);
        }
        return descriptor;
    }

    internal static bool IsSha256(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$");

    private static void ValidateArchivePath(string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/').Any(s => s is ".." or ""))
            throw new InvalidDataException($"unsafe path in bundle descriptor: '{path}'");
    }
}

/// <summary>Verified installation of a user-supplied bundle into the per-user
/// media folder. Every hash is checked before anything is installed; the staged
/// unit then has to prove the full encoding contract with its exact staged
/// binaries before activation; activation keeps rollback backups so any error
/// restores the previous tools byte-for-byte. Direct callers installing into
/// the shared per-user folder must serialize across processes themselves
/// (MediaInstaller holds the folder lock for CLI/Workbench).</summary>
public static class MediaBundleInstaller
{
    public sealed record InstalledTool(string Tool, string Path, string Sha256);
    public sealed record Receipt(string Descriptor, string? Archive, string License, string Source, InstalledTool[] Tools);

    /// <summary>Capability check for the fully staged unit: tool name → exact
    /// staged executable path. MediaInstaller never injects this — production
    /// always runs the real staged encoding validation. Only low-level
    /// hash/layout tests substitute it.</summary>
    public delegate Task ValidateStaged(IReadOnlyDictionary<string, string> stagedPaths, CancellationToken ct);

    public static async Task<Receipt> Install(string descriptorPath, string currentRid, MediaDistributions.UnitLayout unit,
        string targetFolder, Action<string> log, CancellationToken ct, ValidateStaged? validateStaged = null)
    {
        var descriptorFullPath = Path.GetFullPath(descriptorPath);
        if (!File.Exists(descriptorFullPath))
            throw new FileNotFoundException($"bundle descriptor does not exist: {descriptorFullPath}");
        var descriptor = MediaBundleDescriptor.Parse(await File.ReadAllTextAsync(descriptorFullPath, ct), currentRid, unit);
        var receipt = await InstallDescriptor(descriptor, Path.GetDirectoryName(descriptorFullPath)!, unit, targetFolder, log, ct, validateStaged);
        return receipt with { Descriptor = descriptorFullPath };
    }

    /// <summary>Install an already-parsed descriptor whose relative paths resolve
    /// against baseFolder. Used by both the user-bundle flow and curated downloads
    /// (where the pinned archive was already fetched to a local temp file).</summary>
    public static async Task<Receipt> InstallDescriptor(MediaBundleDescriptor descriptor, string baseFolder,
        MediaDistributions.UnitLayout unit, string targetFolder, Action<string> log, CancellationToken ct,
        ValidateStaged? validateStaged = null)
    {
        Directory.CreateDirectory(targetFolder);
        var staging = Path.Combine(targetFolder, ".staging-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(targetFolder, ".backup-" + Guid.NewGuid().ToString("N"));
        var committed = false;
        Directory.CreateDirectory(staging);
        try
        {
            if (descriptor.Archive != null)
            {
                var archivePath = Path.GetFullPath(Path.Combine(baseFolder, descriptor.Archive));
                if (!File.Exists(archivePath)) throw new FileNotFoundException($"bundle archive does not exist: {archivePath}");
                log("verify archive " + archivePath);
                var archiveHash = await HashFile(archivePath, ct);
                if (archiveHash != descriptor.ArchiveSha256)
                    throw new InvalidDataException($"archive SHA-256 mismatch ({archiveHash}); refusing to install an unverified bundle");
                await ExtractVerified(archivePath, descriptor, staging, log, ct);
            }
            else
            {
                foreach (var tool in unit.Tools)
                {
                    var artifact = descriptor.Files[tool];
                    var filePath = Path.GetFullPath(Path.Combine(baseFolder, artifact.Path));
                    if (!File.Exists(filePath)) throw new FileNotFoundException($"bundle file does not exist: {filePath}");
                    await VerifyInto(filePath, artifact.Sha256, Path.Combine(staging, ToolFileName(tool)), log, ct);
                }
            }

            // Unix exec bit before validation so the staged binaries can run;
            // File.Move preserves the mode into the activated target.
            if (!OperatingSystem.IsWindows())
                foreach (var tool in unit.Tools)
                    File.SetUnixFileMode(Path.Combine(staging, ToolFileName(tool)),
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Staged full validation with the exact staged binaries: hashes say
            // the bytes are the pinned ones, but only a successful encode-stack
            // probe proves the unit actually works. Nothing is activated before
            // this passes, so a failing bundle leaves the old pair untouched.
            log("validate staged unit (" + string.Join(", ", unit.Tools) + ")");
            await (validateStaged ?? ValidateStagedUnit)(StagedPaths(unit, staging), ct);

            // Activation with rollback backups: the previous tools move aside
            // first, and any error while placing the new unit restores them
            // byte-for-byte — the folder never keeps a partial pair.
            var installed = new List<InstalledTool>();
            var activated = new List<(string Target, string? BackupPath, bool Placed)>();
            try
            {
                Directory.CreateDirectory(backup);
                foreach (var tool in unit.Tools)
                {
                    var target = Path.Combine(targetFolder, ToolFileName(tool));
                    var staged = Path.Combine(staging, ToolFileName(tool));
                    string? backupPath = null;
                    if (File.Exists(target))
                    {
                        backupPath = Path.Combine(backup, ToolFileName(tool));
                        File.Move(target, backupPath);
                    }
                    // Track the old file as soon as it moves aside: placing the
                    // new file can itself fail and must not orphan the backup.
                    activated.Add((target, backupPath, false));
                    File.Move(staged, target);
                    activated[^1] = (target, backupPath, true);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    installed.Add(new(tool, target, descriptor.Files[tool].Sha256));
                    log("installed " + tool + " -> " + target);
                }
            }
            catch (Exception activationError)
            {
                var failures = new List<Exception>();
                for (var i = activated.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (activated[i].Placed && File.Exists(activated[i].Target)) File.Delete(activated[i].Target);
                        if (activated[i].BackupPath is { } restore) File.Move(restore, activated[i].Target);
                    }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    { failures.Add(restoreError); }
                }
                if (failures.Count > 0)
                    throw new IOException("Media activation failed and recovery needs attention; previous files are preserved in " + backup,
                        new AggregateException(new[] { activationError }.Concat(failures)));
                throw;
            }
            committed = true;
            return new("", descriptor.Archive, descriptor.License, descriptor.Source, installed.ToArray());
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            // Never erase a backup that rollback could not restore.
            try { if (Directory.Exists(backup) && (committed || !Directory.EnumerateFileSystemEntries(backup).Any())) Directory.Delete(backup, true); } catch (IOException) { }
        }
    }

    private static IReadOnlyDictionary<string, string> StagedPaths(MediaDistributions.UnitLayout unit, string staging) =>
        unit.Tools.ToDictionary(tool => tool, tool => Path.Combine(staging, ToolFileName(tool)));

    /// <summary>Default staged validation: run the complete encoding contract
    /// against the exact staged executables (never env overrides).</summary>
    private static Task ValidateStagedUnit(IReadOnlyDictionary<string, string> staged, CancellationToken ct)
    {
        if (!staged.TryGetValue("ffmpeg", out var ffmpeg) || !staged.TryGetValue("ffprobe", out var ffprobe))
            throw new InvalidOperationException("The built-in capability check requires the ffmpeg+ffprobe install unit.");
        return MediaTools.EnsureEncoding(MediaProfile.Default, true, ffmpeg, ffprobe, ct);
    }

    private static async Task ExtractVerified(string archivePath, MediaBundleDescriptor descriptor, string staging,
        Action<string> log, CancellationToken ct)
    {
        var wanted = descriptor.Files.ToDictionary(kv => kv.Value.Path.Replace('\\', '/'), kv => kv.Key);
        var found = new HashSet<string>();
        var lower = archivePath.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!wanted.TryGetValue(name, out var tool)) continue;
                log("extract " + entry.FullName);
                await VerifyInto(() => entry.Open(), entry.Length, descriptor.Files[tool].Sha256, Path.Combine(staging, ToolFileName(tool)), log, ct);
                found.Add(tool);
            }
        }
        else if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
        {
            await using var raw = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                if (entry.EntryType is not TarEntryType.RegularFile) continue;
                var name = entry.Name.Replace('\\', '/').TrimStart('.', '/');
                if (!wanted.TryGetValue(name, out var tool)) continue;
                log("extract " + entry.Name);
                await using (var stream = entry.DataStream!)
                    await VerifyInto(() => stream, entry.Length, descriptor.Files[tool].Sha256, Path.Combine(staging, ToolFileName(tool)), log, ct);
                found.Add(tool);
            }
        }
        else throw new InvalidDataException($"unsupported archive type (need .zip/.tar.gz/.tgz): {archivePath}");

        var missing = descriptor.Files.Keys.Where(t => !found.Contains(t)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"archive does not contain {string.Join(", ", missing.Select(t => descriptor.Files[t].Path))}; the bundle is incomplete and was not installed");
    }

    private static Task VerifyInto(string sourceFile, string expectedSha256, string stagedPath, Action<string> log, CancellationToken ct) =>
        VerifyInto(() => File.OpenRead(sourceFile), new FileInfo(sourceFile).Length, expectedSha256, stagedPath, log, ct);

    /// <summary>Stream the payload through SHA-256 into the staging file;
    /// a mismatch deletes the staged bytes and refuses the whole bundle.</summary>
    private static async Task VerifyInto(Func<Stream> open, long length, string expectedSha256, string stagedPath,
        Action<string> log, CancellationToken ct)
    {
        await using var input = open();
        await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new byte[1024 * 256];
        long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (hash != expectedSha256)
        {
            output.Close();
            File.Delete(stagedPath);
            throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(stagedPath)} (got {hash}); the bundle was not installed");
        }
        log($"sha256 ok ({length} bytes)");
    }

    private static async Task<string> HashFile(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    public static string ToolFileName(string tool) => tool + (OperatingSystem.IsWindows() ? ".exe" : "");
}
