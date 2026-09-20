namespace GameDirector.Client;

// Rendering owns outputs, never the game's source tree. Resolve existing links
// before comparing so a cache symlink cannot redirect a writer into Assets.
public static class OutputPolicy
{
    public static string Physical(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget != null)
                current = entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unresolved storage link: " + current);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static bool Contains(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);
    }
    public static string Child(string root, params string[] parts)
    {
        var parent = Physical(root);
        // Parent owners validate once on admission; children resolve links on every use.
        var result = Physical(Path.Combine(new[] { parent }.Concat(parts).ToArray()));
        if (!Contains(parent, result)) throw new IOException("Output child escaped its owned directory.");
        return result;
    }
    public static string ManifestRoot(GameDirector.Core.Dsl.CapabilityManifest manifest)
    {
        if (manifest.Capabilities == null || !manifest.Capabilities.TryGetValue(GameDirector.Core.Dsl.CapabilityKeys.ProjectSourceRoot, out var root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new IOException("Adapter must declare an existing " + GameDirector.Core.Dsl.CapabilityKeys.ProjectSourceRoot + " before writing capture output.");
        return Physical(root);
    }
    public static string RequireOutput(string output, string? sourceRoot = null)
    {
        var path = Physical(output);
        if (!string.IsNullOrWhiteSpace(sourceRoot) && Contains(Physical(sourceRoot), path))
            throw new IOException("Production output must be outside the game source project. Choose a Workbench storage folder.");
        for (var dir = Directory.Exists(path) ? new DirectoryInfo(path) : Directory.GetParent(path); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { "check-ignore", "--quiet", "--", path }) start.ArgumentList.Add(arg);
                try { using var process = System.Diagnostics.Process.Start(start)!; if (!process.WaitForExit(5000)) { process.Kill(); throw new IOException("Could not verify ignored output directory."); } if (process.ExitCode != 0) throw new IOException("Output inside a repository must be in an explicitly ignored directory. Choose external Workbench storage."); }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("Cannot verify repository output without Git; choose an external folder."); }
            }
            if (File.Exists(Path.Combine(dir.FullName, "package.json")) || Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")) && Directory.Exists(Path.Combine(dir.FullName, "Assets")) ||
                File.Exists(Path.Combine(dir.FullName, "project.godot")) || Directory.Exists(dir.FullName) && Directory.EnumerateFiles(dir.FullName, "*.uproject", SearchOption.TopDirectoryOnly).Any())
                throw new IOException("Production output cannot be written inside an engine or Node source project.");
        }
        return path;
    }
}
