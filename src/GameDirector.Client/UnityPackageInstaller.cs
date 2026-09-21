using System.Security.Cryptography;
using System.Text.Json;

namespace GameDirector.Client;

/// <summary>Explicit, repeatable Unity package install/upgrade. Default is a
/// read-only plan; --apply writes. Semantics mirror tools/install_unity_package.py
/// so the Python and .NET installers are interchangeable against the same project:
/// an identical repeat install performs zero writes, local edits abort before any
/// write, and owned files removed upstream are deleted only when untouched.
/// Never runs during filming; no game settings, scenes or credentials are touched.</summary>
public static class UnityPackageInstaller
{
    public const string ReceiptFileName = ".gamedirector-install.json";
    public const string SyncFileName = ".gamedirector-sync.json";
    private static readonly string[] ExcludedNames = { ".DS_Store", SyncFileName, ReceiptFileName };

    public sealed record Result(
        string Package, string Version, string Destination, string Mode,
        string[] ChangedFiles, string[] RemovedOwnedFiles, bool ReceiptChanged, int Writes);

    public sealed record Conflict(string Path, string Reason);
    public sealed class InstallConflictException : InvalidOperationException
    {
        public IReadOnlyList<Conflict> Conflicts { get; }
        public InstallConflictException(IReadOnlyList<Conflict> conflicts)
            : base("Local package modifications would be lost; aborting before any write. " +
                  "Restore the listed files from the package source or delete them and retry: " +
                  string.Join(", ", conflicts.Select(c => c.Path)))
        { Conflicts = conflicts; }
    }

    /// <summary>Default package source: the Unity package staged inside a full
    /// GameDirector distribution (declared by the distribution manifest). A dev
    /// checkout is never used implicitly.</summary>
    public static string DefaultSource()
    {
        var root = DistributionManifest.FindRoot();
        if (root == null)
            throw new InvalidOperationException(
                "This gd is not part of a full GameDirector distribution, so no bundled Unity package exists. " +
                "Pass --source <path to com.gamedirector.unity> from a full distribution (tools/package_release.py output), " +
                "or build the Unity bundle with tools/package_unity_candidate.py.");
        var manifest = DistributionManifest.Load(Path.Combine(root, DistributionManifest.ManifestFileName));
        var source = manifest.UnityPackageSource(root);
        if (!File.Exists(Path.Combine(source, "package.json")))
            throw new InvalidOperationException(
                $"The distribution at {root} does not carry the Unity package at {manifest.Unity.DistributionFolder}. " +
                "Pass --source <path to com.gamedirector.unity> explicitly.");
        return source;
    }

    public static Result Install(string sourcePath, string projectPath, bool apply)
    {
        var source = Path.GetFullPath(sourcePath);
        var project = Path.GetFullPath(projectPath);
        if (!Directory.Exists(source)) throw new ArgumentException($"Package source does not exist: {source}");
        if (!Directory.Exists(Path.Combine(project, "Assets")) || !Directory.Exists(Path.Combine(project, "ProjectSettings")))
            throw new ArgumentException($"Choose a Unity project root (a folder containing Assets and ProjectSettings): {project}");

        var packageJson = Path.Combine(source, "package.json");
        if (!File.Exists(packageJson)) throw new ArgumentException($"Package source has no package.json: {source}");
        string name; string version;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            name = doc.RootElement.GetProperty("name").GetString() ?? "";
            version = doc.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ArgumentException($"Package source package.json is not a valid Unity package manifest: {ex.Message}");
        }
        if (!name.StartsWith("com.gamedirector.", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException($"Unexpected package identity '{name}'.");

        var packagesDir = Path.Combine(project, "Packages");
        var target = Path.Combine(packagesDir, name);
        if (IsReparsePoint(packagesDir) || IsReparsePoint(target))
            throw new ArgumentException("Package destination must not be a symlink or junction.");
        var receipt = Path.Combine(target, ReceiptFileName);
        if (IsReparsePoint(receipt)) throw new ArgumentException("Install receipt must not be a symlink or junction.");

        var old = ReadReceipt(receipt, target);
        var desired = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(source).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (ExcludedNames.Contains(Path.GetFileName(file))) continue;
            var relative = NormalizeRelative(Path.GetRelativePath(source, file));
            if (IsReparsePoint(file)) throw new ArgumentException($"Package source contains a symlink, refusing to install: {relative}");
            desired[relative] = File.ReadAllBytes(file);
        }

        var conflicts = new List<Conflict>();
        var changes = new List<string>();
        foreach (var (relative, data) in desired)
        {
            var file = Path.Combine(target, RelativeToNative(relative));
            RefuseLinkedAncestors(target, file);
            if (Directory.Exists(file))
            {
                conflicts.Add(new(relative, "a directory occupies the package file path"));
                continue;
            }
            if (File.Exists(file))
            {
                if (IsReparsePoint(file)) { conflicts.Add(new(relative, "installed path is a symlink")); continue; }
                var actual = File.ReadAllBytes(file);
                if (actual.AsSpan().SequenceEqual(data)) continue;
                if (!old.TryGetValue(relative, out var recorded) || Digest(actual) != recorded)
                {
                    conflicts.Add(new(relative, "preserved local package edit"));
                    continue;
                }
            }
            changes.Add(relative);
        }
        var removed = new List<string>();
        foreach (var (relative, expected) in old)
        {
            var file = Path.Combine(target, RelativeToNative(relative));
            RefuseLinkedAncestors(target, file);
            if (desired.ContainsKey(relative) || !File.Exists(file)) continue;
            if (IsReparsePoint(file) || Digest(File.ReadAllBytes(file)) != expected)
            {
                conflicts.Add(new(relative, "locally modified file the package no longer ships"));
                continue;
            }
            removed.Add(relative);
        }
        if (conflicts.Count > 0) throw new InstallConflictException(conflicts);

        var receiptData = RenderReceipt(name, version, desired);
        var receiptChanged = !File.Exists(receipt) || !File.ReadAllBytes(receipt).AsSpan().SequenceEqual(receiptData);

        if (apply)
        {
            Directory.CreateDirectory(target);
            foreach (var relative in changes)
            {
                var file = Path.Combine(target, RelativeToNative(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                WriteAtomic(file, desired[relative]);
            }
            foreach (var relative in removed) File.Delete(Path.Combine(target, RelativeToNative(relative)));
            if (receiptChanged) WriteAtomic(receipt, receiptData);
        }
        return new(name, version, target, apply ? "installed" : "plan",
            changes.ToArray(), removed.ToArray(), receiptChanged,
            changes.Count + removed.Count + (receiptChanged ? 1 : 0));
    }

    /// <summary>Parse the receipt left by a previous install (Python or .NET).
    /// Malformed JSON or escaping paths are honest errors, never silent reuse.</summary>
    internal static SortedDictionary<string, string> ReadReceipt(string receipt, string target)
    {
        var empty = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(receipt)) return empty;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(receipt)); }
        catch (JsonException ex) { throw new InvalidDataException($"Install receipt is unreadable ({ReceiptFileName}): {ex.Message}. Delete {receipt} to reinstall cleanly."); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Install receipt lacks a files map: {receipt}. Delete it to reinstall cleanly.");
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in files.EnumerateObject())
            {
                var relative = NormalizeRelative(entry.Name);
                // Traversal guard: a receipt path must stay inside the installed package.
                var combined = Path.GetFullPath(Path.Combine(target, RelativeToNative(relative)));
                if (!combined.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException($"Invalid install receipt path '{entry.Name}' in {receipt}; refusing to trust it.");
                var hash = entry.Value.GetString();
                if (hash == null || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
                    throw new InvalidDataException($"Install receipt has a malformed hash for '{entry.Name}'.");
                map[relative] = hash;
            }
            return map;
        }
    }

    internal static byte[] RenderReceipt(string name, string version, SortedDictionary<string, byte[]> desired)
    {
        // Stable shape shared with the Python installer: sorted keys, two-space indent.
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, data) in desired) files[relative] = Digest(data);
        // Same field order/shape as the Python installer (package, version, files;
        // sorted file keys) so receipts stay interchangeable across both installers.
        var ordered = new Dictionary<string, object> { ["package"] = name, ["version"] = version, ["files"] = files };
        var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    internal static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void WriteAtomic(string file, byte[] data)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, ".gd-install-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temp, data); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Reject a destination when any directory between the package root
    /// and the file is a symlink/junction: writes would escape the package.</summary>
    private static void RefuseLinkedAncestors(string target, string file)
    {
        var targetFull = Path.GetFullPath(target);
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (dir != null && dir.Length > targetFull.Length && dir.StartsWith(targetFull, StringComparison.Ordinal))
        {
            if (IsReparsePoint(dir)) throw new ArgumentException($"Symlink or junction inside package path: {Path.GetRelativePath(targetFull, dir)}");
            if (File.Exists(dir)) throw new ArgumentException($"A file occupies a package directory path: {Path.GetRelativePath(targetFull, dir)}");
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        if (IsReparsePoint(directory)) throw new ArgumentException("Package source directory is a symlink or junction: " + directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsReparsePoint(entry)) throw new ArgumentException("Package source contains a symlink or junction: " + entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in SourceFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static string NormalizeRelative(string path) => path.Replace('\\', '/');
    internal static string RelativeToNative(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);
}
