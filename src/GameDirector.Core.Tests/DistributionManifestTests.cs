using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

[CollectionDefinition("DistributionEnv")]
public class DistributionEnvCollection { }

/// <summary>The declarative distribution manifest: the repo copy must stay
/// valid, schema/product drift is rejected, sibling resolution honors platform
/// executable extensions, and the runtime manifest gates compatibility.</summary>
[Collection("DistributionEnv")]
public class DistributionManifestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "distribution.json")))
            dir = dir.Parent;
        Assert.True(dir != null, "repo root with tools/distribution.json not found above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    [Fact]
    public void RepoManifestIsValidAndComplete()
    {
        var manifest = DistributionManifest.Load(Path.Combine(RepoRoot(), "tools", "distribution.json"));
        Assert.Equal("GameDirector", manifest.Product);
        Assert.Equal(1, manifest.ProtocolVersion);
        Assert.Contains("win-x64", manifest.SupportedPlatforms);
        Assert.Contains("osx-arm64", manifest.SupportedPlatforms);
        foreach (var rid in manifest.SupportedPlatforms)
            Assert.True(manifest.Platforms.ContainsKey(rid), $"platform {rid} lacks layout");
        foreach (var app in new[] { "cli", "mcp", "workbench" })
            Assert.True(manifest.Apps.ContainsKey(app), $"missing app {app}");
        Assert.Equal("gd.exe", Path.GetFileName(manifest.SiblingExecutable("cli", "/dist", "win-x64")));
        Assert.Equal("gd", Path.GetFileName(manifest.SiblingExecutable("cli", "/dist", "osx-arm64")));
        var unitySource = manifest.UnityPackageSource("/dist");
        Assert.EndsWith("com.gamedirector.unity", unitySource);
        Assert.StartsWith("/dist", unitySource);
    }

    [Fact]
    public void VersionComesFromBuildMetadata()
    {
        // The runtime version string must equal Directory.Build.props <Version>,
        // which the build stamps into the assembly — no copied literal anywhere.
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var expected = props.Descendants("Version").Single().Value;
        Assert.Equal(expected, DistributionManifest.ProductVersion);
    }

    [Fact]
    public void UnknownSchemaAndProductAreRejected()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "distribution.json"));
        Assert.Throws<InvalidDataException>(() => DistributionManifest.Parse(manifest.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99")));
        Assert.Throws<InvalidDataException>(() => DistributionManifest.Parse(manifest.Replace("\"product\": \"GameDirector\"", "\"product\": \"Other\"")));
    }

    [Fact]
    public void UnsupportedPlatformIsRejected()
    {
        var manifest = DistributionManifest.Load(Path.Combine(RepoRoot(), "tools", "distribution.json"));
        Assert.Throws<InvalidOperationException>(() => manifest.SiblingExecutable("cli", "/dist", "freebsd-x64"));
    }

    [Fact]
    public void RuntimeManifestGatesPlatformAndProtocol()
    {
        var runtime = new RuntimeManifest
        {
            Product = "GameDirector", Version = "1", Rid = "win-x64", ProtocolVersion = 1, App = "workbench",
        };
        Assert.Null(runtime.CompatibilityFailure("win-x64", 1));
        Assert.Contains("osx-arm64", runtime.CompatibilityFailure("osx-arm64", 1));
        Assert.Contains("protocol", runtime.CompatibilityFailure("win-x64", 2));
        var parsed = RuntimeManifest.Parse(System.Text.Json.JsonSerializer.Serialize(runtime,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.Equal("win-x64", parsed.Rid);
        Assert.Throws<InvalidDataException>(() => RuntimeManifest.Parse("{\"product\":\"Other\",\"version\":\"1\",\"rid\":\"win-x64\"}"));
    }

    [Fact]
    public void RuntimeManifestVerifiesDeclaredHashes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gd-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "tool"), new byte[] { 1, 2, 3 });
            var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
            var runtime = new RuntimeManifest
            {
                Product = "GameDirector", Version = "1", Rid = "win-x64", ProtocolVersion = 1, App = "x",
                Executables = new[] { "tool" },
                Sha256 = new Dictionary<string, string> { ["tool"] = good },
            };
            Assert.Null(runtime.IntegrityFailure(folder));
            Assert.Null(runtime.IntegrityFailure(folder, "tool"));
            // Undeclared file: nothing to verify against.
            Assert.Null(runtime.IntegrityFailure(folder, "other"));
            runtime.Sha256["tool"] = new string('0', 64);
            Assert.Contains("modified or corrupted", runtime.IntegrityFailure(folder));
            runtime.Sha256["tool"] = good;
            runtime.Sha256["missing"] = good;
            Assert.Contains("missing", runtime.IntegrityFailure(folder));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RuntimeManifestRejectsUnsafeDeclaredPaths()
    {
        var runtime = new RuntimeManifest
        {
            Product = "GameDirector", Version = "1", Rid = "win-x64", ProtocolVersion = 1, App = "x",
            Executables = new[] { "tool" },
            Sha256 = new Dictionary<string, string> { ["tool"] = new string('a', 64) },
        };
        runtime.ValidateDeclaredPaths(); // fine
        runtime.Sha256["../escape"] = new string('a', 64);
        Assert.Throws<InvalidDataException>(runtime.ValidateDeclaredPaths);
        var rooted = new RuntimeManifest
        {
            Product = "GameDirector", Version = "1", Rid = "win-x64", ProtocolVersion = 1, App = "x",
            Executables = new[] { "/bin/sh" },
        };
        Assert.Throws<InvalidDataException>(rooted.ValidateDeclaredPaths);
        Assert.Throws<InvalidDataException>(() => runtime.IntegrityFailure("/anywhere", "a//b"));
    }

    [Fact]
    public void RootOverrideHonorsExplicitness()
    {
        var previous = Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable);
        var folder = Path.Combine(Path.GetTempPath(), "gd-dist-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, folder);
            // Set but missing manifest: honest failure, never silent fallback.
            Assert.Throws<InvalidOperationException>(DistributionManifest.FindRoot);
            File.Copy(Path.Combine(RepoRoot(), "tools", "distribution.json"), Path.Combine(folder, "distribution.json"));
            Assert.Equal(Path.GetFullPath(folder), DistributionManifest.FindRoot());
            Assert.NotNull(DistributionManifest.LoadFromDistribution());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, previous);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
