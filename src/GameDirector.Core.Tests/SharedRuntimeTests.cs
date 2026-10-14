using System.Security.Cryptography;
using System.Text.Json;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

[Collection("DistributionEnv")]
public sealed class SharedRuntimeTests : TempPaths
{
    private (string root, string bin, string package) Fixture()
    {
        var root = TempPath("gd-shared-runtime-");
        var bin = Path.Combine(root, "bin");
        var package = Path.Combine(root, "unity/com.gamedirector.unity");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(package);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        File.Copy(Path.Combine(repo, "tools/distribution.json"), Path.Combine(root, "distribution.json"));
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var apps = new Dictionary<string, string> { ["cli"] = "gd" + extension,
            ["mcp"] = "gamedirector-mcp" + extension, ["workbench"] = "gamedirector-workbench" + extension };
        var hashes = new Dictionary<string, string>();
        foreach (var name in apps.Values.Concat(new[] { "gd.dll", "gamedirector-mcp.dll", "shared-native", "Fonts/font.otf" }))
        {
            var file = Path.Combine(bin, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, name);
            hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        }
        File.WriteAllText(Path.Combine(bin, "gamedirector.runtime.json"), JsonSerializer.Serialize(new {
            product = "GameDirector", version = "0.1.0", unityPackageVersion = "0.1.0", app = "shared",
            apps, rid = DistributionManifest.CurrentRid(), protocolVersion = 1,
            executables = apps.Values.ToArray(), sha256 = hashes }));
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.gamedirector.unity\",\"version\":\"0.1.0\"}");
        return (root, bin, package);
    }

    private static void WithRoot(string root, Action body)
    {
        var old = Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable);
        try { Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, root); body(); }
        finally { Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, old); }
    }

    [Fact]
    public void AppContextStyleTrailingSlashDoesNotBreakIntegrityWalk()
    {
        var (_, bin, _) = Fixture();
        var manifest = RuntimeManifest.Load(Path.Combine(bin, "gamedirector.runtime.json"));
        Assert.Null(manifest.IntegrityFailure(bin + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void OneManifestLaunchesEveryDeclaredEntryPoint()
    {
        var (root, bin, _) = Fixture();
        WithRoot(root, () => {
            foreach (var app in new[] { "cli", "mcp", "workbench" })
                Assert.Equal(bin, Path.GetDirectoryName(RuntimeLauncher.ResolveSibling(app)));
        });
    }

    [Theory]
    [InlineData("gd.dll")]
    [InlineData("gamedirector-mcp.dll")]
    [InlineData("shared-native")]
    [InlineData("Fonts/font.otf")]
    public void AnyPayloadTamperBlocksEveryEntryPoint(string name)
    {
        var (root, bin, _) = Fixture(); File.WriteAllText(Path.Combine(bin, name), "corrupted");
        WithRoot(root, () => {
            foreach (var app in new[] { "cli", "mcp", "workbench" })
                Assert.Contains(name, Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling(app)).Message);
        });
    }

    [Fact]
    public void ExistingUnityMirrorMarkerDoesNotInvalidateTheSharedRuntime()
    {
        var (root, bin, _) = Fixture(); File.WriteAllText(Path.Combine(bin, ".complete"), "published mirror");
        WithRoot(root, () => Assert.StartsWith(bin, RuntimeLauncher.ResolveSibling("mcp")));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("._gd")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("Icon\r")]
    public void OsMetadataAtRootAndInSubfoldersDoesNotBlockLaunchOrInstall(string name)
    {
        if (OperatingSystem.IsWindows() && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            // Finder's Icon<CR> cannot exist on Windows; classify it without I/O.
            Assert.True(GameDirector.Core.Dsl.RuntimeFilePolicy.IsOsMetadata(name));
            return;
        }
        var (root, bin, package) = Fixture();
        foreach (var folder in new[] { bin, Path.Combine(bin, "Fonts") })
            File.WriteAllText(Path.Combine(folder, name), "OS metadata");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        WithRoot(root, () => {
            foreach (var app in new[] { "cli", "mcp", "workbench" })
                Assert.StartsWith(bin, RuntimeLauncher.ResolveSibling(app));
            Assert.Contains("Tools~/bin/gd.dll", UnityPackageInstaller.Install(package, project, false).ChangedFiles);
        });
    }

    [Fact]
    public void MetadataLookalikesAreStillFatal()
    {
        var (root, bin, _) = Fixture(); File.WriteAllText(Path.Combine(bin, ".DS_Store.dll"), "payload");
        WithRoot(root, () => Assert.Contains(".DS_Store.dll",
            Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("mcp")).Message));
    }

    [Fact]
    public void UnlistedPayloadIsRefusedEvenWhenDeclaredFilesMatch()
    {
        var (root, bin, _) = Fixture(); File.WriteAllText(Path.Combine(bin, "injected.dll"), "unexpected program");
        WithRoot(root, () => Assert.Contains("injected.dll",
            Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("mcp")).Message));
    }

    [Fact]
    public void LinkedPayloadCannotEscapeTheDeclaredTree()
    {
        var (root, bin, _) = Fixture(); var fonts = Path.Combine(bin, "Fonts");
        var external = Path.Combine(root, "external-fonts"); Directory.Move(fonts, external);
        TestDirectoryLinks.Create(fonts, external);
        WithRoot(root, () => Assert.Contains("link",
            Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("mcp")).Message));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArchiveSourceWithTrailingSeparatorStillIncludesTheVerifiedRuntime(bool apply)
    {
        var (root, _, package) = Fixture(); var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets")); Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        WithRoot(root, () => {
            var result = UnityPackageInstaller.Install(package + Path.DirectorySeparatorChar, project, apply);
            Assert.Contains("Tools~/bin/gd.dll", result.ChangedFiles);
            if (apply) Assert.True(File.Exists(Path.Combine(result.Destination, "Tools~/bin/gd.dll")));
        });
    }

    [Fact]
    public void CaseVariantOnCaseInsensitiveFilesystemStillIncludesRuntime()
    {
        var (root, _, package) = Fixture(); var project = Path.Combine(root, "project");
        var variant = Path.Combine(Path.GetDirectoryName(package)!, Path.GetFileName(package).ToUpperInvariant());
        if (!Directory.Exists(variant)) return; // case-sensitive volume: not an alias
        Directory.CreateDirectory(Path.Combine(project, "Assets")); Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        WithRoot(root, () => Assert.Contains("Tools~/bin/gd.dll", UnityPackageInstaller.Install(variant, project, false).ChangedFiles));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SourceWithoutRuntimeIsRefusedBeforeAnyWrite(bool apply)
    {
        var (root, _, package) = Fixture(); var standalone = Path.Combine(root, "standalone");
        Directory.CreateDirectory(standalone); File.Copy(Path.Combine(package, "package.json"), Path.Combine(standalone, "package.json"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        WithRoot(root, () => Assert.Contains("runtime", Assert.Throws<InvalidDataException>(
            () => UnityPackageInstaller.Install(standalone, project, apply)).Message, StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Path.Combine(project, "Packages")));
    }

    [Fact]
    public void AppliedArchiveInstallSetsOnlyDeclaredUnixExecutables()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, bin, package) = Fixture(); var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets")); Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        WithRoot(root, () => {
            var result = UnityPackageInstaller.Install(package, project, true);
            var runtime = RuntimeManifest.Load(Path.Combine(bin, "gamedirector.runtime.json"));
            if (OperatingSystem.IsWindows()) return;
            foreach (var name in runtime.Executables)
                Assert.Equal(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
                    File.GetUnixFileMode(Path.Combine(result.Destination, "Tools~/bin", name)) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(result.Destination, "Tools~/bin/gd.dll")) & UnixFileMode.UserExecute);
        });
    }

    [Fact]
    public void ArchiveInstallMaterializesExactlyTheSameRuntimeWithoutMutatingSource()
    {
        var (root, bin, package) = Fixture(); var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets")); Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        WithRoot(root, () => {
            var plan = UnityPackageInstaller.Install(UnityPackageInstaller.DefaultSource(), project, false);
            Assert.Contains("Tools~/bin/gd.dll", plan.ChangedFiles);
            Assert.False(Directory.Exists(Path.Combine(project, "Packages")));
            var installed = UnityPackageInstaller.Install(package, project, true);
            foreach (var file in Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories))
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(installed.Destination, "Tools~/bin", Path.GetRelativePath(bin, file))));
            Assert.False(Directory.Exists(Path.Combine(package, "Tools~/bin")));
            Assert.Equal(0, UnityPackageInstaller.Install(package, project, true).Writes);
        });
    }
}
