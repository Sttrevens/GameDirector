using GameDirector.Client;

namespace GameDirector.Core.Tests;

internal static class InstallerRuntimeFixture
{
    internal static void Write(string source, string version)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        File.Copy(Path.Combine(repo, "tools/distribution.json"), Path.Combine(source, "distribution.json"));
        var bin = Path.Combine(source, "Tools~/bin"); Directory.CreateDirectory(bin);
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var apps = new Dictionary<string, string> { ["cli"] = "gd" + ext, ["mcp"] = "gamedirector-mcp" + ext, ["workbench"] = "gamedirector-workbench" + ext };
        foreach (var name in apps.Values) File.WriteAllText(Path.Combine(bin, name), name);
        var hashes = apps.Values.ToDictionary(n => n, n => UnityPackageInstaller.Digest(File.ReadAllBytes(Path.Combine(bin, n))));
        File.WriteAllText(Path.Combine(bin, "gamedirector.runtime.json"), System.Text.Json.JsonSerializer.Serialize(new {
            product = "GameDirector", version, unityPackageVersion = version, rid = DistributionManifest.CurrentRid(),
            app = "shared", apps, protocolVersion = 1, sha256 = hashes, executables = OperatingSystem.IsWindows() ? Array.Empty<string>() : apps.Values.ToArray() }));
    }
}
