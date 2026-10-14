using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>Media distribution manifest and verified bundle installs: the
/// install unit is ffmpeg+ffprobe together; incomplete strategies/descriptors
/// are rejected up front, hashes gate every byte installed, and archives are
/// extracted by declared layout only.</summary>
[Collection("DistributionEnv")]
public class MediaDistributionTests : TempPaths
{
    private static readonly MediaDistributions.UnitLayout Unit =
        new() { Tools = new[] { "ffmpeg", "ffprobe" } };

    private static string BundledManifestPath()
    {
        // Client content flows to the test output; fall back to the repo source.
        var local = Path.Combine(AppContext.BaseDirectory, "media-distributions.json");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "GameDirector.Client", "media-distributions.json")))
            dir = dir.Parent;
        Assert.True(dir != null, "media-distributions.json not found");
        return Path.Combine(dir!.FullName, "src", "GameDirector.Client", "media-distributions.json");
    }

    [Fact]
    public void MacProbesHomebrewFfmpegFullBeforePlainHomebrewBin()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var folders = MediaTools.WellKnownFolders().ToList();
        Assert.True(folders.IndexOf("/opt/homebrew/opt/ffmpeg-full/bin") < folders.IndexOf("/opt/homebrew/bin"));
        Assert.True(folders.IndexOf("/usr/local/opt/ffmpeg-full/bin") < folders.IndexOf("/usr/local/bin"));
    }

    [Fact]
    public void BundledManifestIsCompleteAndHonest()
    {
        var manifest = MediaDistributions.Load(BundledManifestPath());
        Assert.Contains("ffmpeg", manifest.Unit.Tools);
        Assert.Contains("ffprobe", manifest.Unit.Tools);
        // brew/winget stay as the supported complete package-manager strategies.
        Assert.Contains(manifest.Strategies, s => s.Id == "winget-ffmpeg" && s.Provides.Contains("ffprobe"));
        Assert.Contains(manifest.Strategies, s => s.Id == "brew-ffmpeg" && s.Provides.Contains("ffprobe"));
        // Homebrew's plain ffmpeg formula has no libass; only ffmpeg-full renders captions.
        Assert.Equal(new[] { "install", "ffmpeg-full" }, manifest.Strategies.Single(s => s.Id == "brew-ffmpeg").Args);
        // No curated download is advertised until one is hash-verified complete;
        // crucially the old ffmpeg-only Mac pin is gone.
        Assert.DoesNotContain(manifest.Downloads, d => !d.Files.Keys.Contains("ffprobe"));
        Assert.NotEmpty(manifest.UserBundle.Id);
    }

    [Fact]
    public void IncompleteStrategyIsRejectedAtParse()
    {
        var json = @"{""schemaVersion"":1,""unit"":{""tools"":[""ffmpeg"",""ffprobe""]},
            ""strategies"":[{""id"":""partial"",""kind"":""packageManager"",""rids"":[""win-x64""],""provides"":[""ffmpeg""]}]}";
        var ex = Assert.Throws<InvalidDataException>(() => MediaDistributions.Parse(json));
        Assert.Contains("incomplete", ex.Message);
    }

    [Fact]
    public void IncompleteDownloadIsRejectedAtParse()
    {
        var json = @"{""schemaVersion"":1,""unit"":{""tools"":[""ffmpeg"",""ffprobe""]},
            ""downloads"":[{""id"":""mac-ffmpeg-only"",""rids"":[""osx-arm64""],""url"":""https://example.invalid/f"",
            ""archiveSha256"":""" + new string('a', 64) + @""",""files"":{""ffmpeg"":{""path"":""ffmpeg"",""sha256"":""" + new string('b', 64) + @"""}}}]}";
        Assert.Throws<InvalidDataException>(() => MediaDistributions.Parse(json));
    }

    [Fact]
    public void DescriptorMustProvideTheWholeUnit()
    {
        var json = @"{""schemaVersion"":1,""files"":{""ffmpeg"":{""path"":""bin/ffmpeg"",""sha256"":""" + new string('a', 64) + @"""}}}";
        var ex = Assert.Throws<InvalidDataException>(() => MediaBundleDescriptor.Parse(json, "win-x64", Unit));
        Assert.Contains("ffprobe", ex.Message);
    }

    [Fact]
    public void DescriptorRejectsRidMismatchBadHashesAndTraversal()
    {
        var good = @"{""schemaVersion"":1,""rid"":""RID"",""files"":{""ffmpeg"":{""path"":""bin/ffmpeg"",""sha256"":""" + new string('a', 64)
            + @"""},""ffprobe"":{""path"":""bin/ffprobe"",""sha256"":""" + new string('b', 64) + @"""}}}";
        var rid = DistributionManifest.CurrentRid();
        Assert.Throws<InvalidDataException>(() => MediaBundleDescriptor.Parse(good.Replace("RID", "other-rid"), rid, Unit));
        Assert.Throws<InvalidDataException>(() => MediaBundleDescriptor.Parse(good.Replace("RID", rid).Replace(new string('a', 64), "not-a-hash"), rid, Unit));
        Assert.Throws<InvalidDataException>(() => MediaBundleDescriptor.Parse(good.Replace("RID", rid).Replace("bin/ffmpeg", "../ffmpeg"), rid, Unit));
        var parsed = MediaBundleDescriptor.Parse(good.Replace("RID", rid), rid, Unit);
        Assert.Equal(rid, parsed.Rid);
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private (string folder, string descriptorPath) MakeBundle(byte[] ffmpeg, byte[] ffprobe, bool tamper = false)
    {
        var folder = TempPath("gd-bundle-");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "ffmpeg-bin"), ffmpeg);
        File.WriteAllBytes(Path.Combine(folder, "ffprobe-bin"), ffprobe);
        var descriptor = new
        {
            schemaVersion = 1,
            license = "test license",
            source = "test fixture",
            files = new Dictionary<string, object>
            {
                ["ffmpeg"] = new { path = "ffmpeg-bin", sha256 = Sha(ffmpeg) },
                ["ffprobe"] = new { path = "ffprobe-bin", sha256 = tamper ? new string('0', 64) : Sha(ffprobe) },
            },
        };
        var path = Path.Combine(folder, "bundle.json");
        File.WriteAllText(path, JsonSerializer.Serialize(descriptor));
        return (folder, path);
    }

    /// <summary>No-op capability check for low-level hash/layout tests: these
    /// fixtures carry a few bytes, not a working ffmpeg, so they substitute the
    /// staged validation. Production (MediaInstaller) never injects this.</summary>
    private static readonly MediaBundleInstaller.ValidateStaged SkipValidation = (_, _) => Task.CompletedTask;

    private static void AssertNoScratchFolders(string target)
    {
        if (!Directory.Exists(target)) return;
        Assert.Empty(Directory.EnumerateDirectories(target)
            .Where(d => Path.GetFileName(d).StartsWith(".staging") || Path.GetFileName(d).StartsWith(".backup")));
    }

    [Fact]
    public async Task LinkedDescriptorIsRejectedBeforeStagingOrExecution()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation requires privileges
        var (folder, descriptor) = MakeBundle("fake-ffmpeg"u8.ToArray(), "fake-ffprobe"u8.ToArray());
        var link = Path.Combine(folder, "linked.json"); File.CreateSymbolicLink(link, descriptor);
        var target = Path.Combine(folder, "target"); var executed = false;
        try {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => MediaBundleInstaller.Install(link,
                DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None,
                (_, _) => { executed = true; return Task.CompletedTask; }));
            Assert.Contains("regular file", error.Message); Assert.False(executed); Assert.False(Directory.Exists(target));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void DescriptorDirectoryAndLinkedParentAreRejected()
    {
        var (folder, descriptor) = MakeBundle("fake-ffmpeg"u8.ToArray(), "fake-ffprobe"u8.ToArray());
        var link = TempPath("gd-bundle-linked-");
        try {
            Assert.Throws<ArgumentException>(() => MediaBundleDescriptor.RequireFile(folder));
            TestDirectoryLinks.Create(link, folder);
            Assert.Throws<ArgumentException>(() => MediaBundleDescriptor.RequireFile(Path.Combine(link, Path.GetFileName(descriptor))));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(folder, true); }
    }

    [Fact]
    public void EmptyAndUnixFifoDescriptorsAreRejectedWithoutOpening()
    {
        var folder = TempPath("gd-descriptor-type-"); Directory.CreateDirectory(folder);
        try {
            var empty = Path.Combine(folder, "empty.json"); File.WriteAllText(empty, "");
            Assert.Throws<ArgumentException>(() => MediaBundleDescriptor.RequireFile(empty));
            if (OperatingSystem.IsWindows()) return;
            var fifo = Path.Combine(folder, "pipe.json");
            var info = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false }; info.ArgumentList.Add(fifo);
            using var process = System.Diagnostics.Process.Start(info)!; Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
            Assert.Throws<ArgumentException>(() => MediaBundleDescriptor.RequireFile(fifo));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task VerifiedBundleInstallsBothTools()
    {
        var (folder, descriptor) = MakeBundle(new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6, 7 });
        var target = Path.Combine(folder, "media");
        var log = new List<string>();
        var receipt = await MediaBundleInstaller.Install(descriptor, DistributionManifest.CurrentRid(), Unit, target, log.Add, CancellationToken.None, SkipValidation);
        Assert.Equal(2, receipt.Tools.Length);
        var ffmpeg = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"));
        var ffprobe = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(ffmpeg));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(ffprobe));
        AssertNoScratchFolders(target);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(ffprobe);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute), "ffprobe must be executable after install");
        }
    }

    [Fact]
    public async Task HashMismatchInstallsNothing()
    {
        var (folder, descriptor) = MakeBundle(new byte[] { 1 }, new byte[] { 2 }, tamper: true);
        var target = Path.Combine(folder, "media");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MediaBundleInstaller.Install(descriptor, DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"))));
        Assert.False(File.Exists(Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe"))));
        AssertNoScratchFolders(target);
    }

    [Fact]
    public async Task ZipArchiveLayoutIsHonoredAndVerified()
    {
        var folder = TempPath("gd-bundle-zip-");
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "bundle.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var f = zip.CreateEntry("bin/ffmpeg"); using (var w = new StreamWriter(f.Open())) w.Write("fake-ffmpeg");
            var p = zip.CreateEntry("bin/ffprobe"); using (var w = new StreamWriter(p.Open())) w.Write("fake-ffprobe");
            zip.CreateEntry("bin/ignore-me");
        }
        var descriptor = new
        {
            schemaVersion = 1,
            archive = "bundle.zip",
            archiveSha256 = Sha(File.ReadAllBytes(archive)),
            files = new Dictionary<string, object>
            {
                ["ffmpeg"] = new { path = "bin/ffmpeg", sha256 = Sha(System.Text.Encoding.UTF8.GetBytes("fake-ffmpeg")) },
                ["ffprobe"] = new { path = "bin/ffprobe", sha256 = Sha(System.Text.Encoding.UTF8.GetBytes("fake-ffprobe")) },
            },
        };
        var descriptorPath = Path.Combine(folder, "bundle.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor));
        var target = Path.Combine(folder, "media");
        var receipt = await MediaBundleInstaller.Install(descriptorPath, DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None, SkipValidation);
        Assert.Equal(2, receipt.Tools.Length);
        Assert.Equal("fake-ffprobe", File.ReadAllText(Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe"))));
        // The undeclared extra entry is never extracted.
        Assert.False(File.Exists(Path.Combine(target, "ignore-me")));
    }

    [Fact]
    public async Task ArchiveMissingAUnitToolIsRefused()
    {
        var folder = TempPath("gd-bundle-zip-");
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "bundle.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var f = zip.CreateEntry("ffmpeg"); using (var w = new StreamWriter(f.Open())) w.Write("fake-ffmpeg");
        }
        var descriptor = new
        {
            schemaVersion = 1,
            archive = "bundle.zip",
            archiveSha256 = Sha(File.ReadAllBytes(archive)),
            files = new Dictionary<string, object>
            {
                ["ffmpeg"] = new { path = "ffmpeg", sha256 = Sha(System.Text.Encoding.UTF8.GetBytes("fake-ffmpeg")) },
                ["ffprobe"] = new { path = "ffprobe", sha256 = new string('1', 64) },
            },
        };
        var descriptorPath = Path.Combine(folder, "bundle.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor));
        var target = Path.Combine(folder, "media");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MediaBundleInstaller.Install(descriptorPath, DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None));
        Assert.Contains("incomplete", ex.Message);
        Assert.False(File.Exists(Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"))));
    }

    [Fact]
    public async Task FailedStagedValidationLeavesOldBytesUntouched()
    {
        var (folder, descriptor) = MakeBundle(new byte[] { 10, 10 }, new byte[] { 20, 20 });
        var target = Path.Combine(folder, "media");
        Directory.CreateDirectory(target);
        var oldFfmpeg = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"));
        var oldFfprobe = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe"));
        File.WriteAllBytes(oldFfmpeg, new byte[] { 1, 1, 1 });
        File.WriteAllBytes(oldFfprobe, new byte[] { 2, 2 });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaBundleInstaller.Install(descriptor, DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None,
                (_, _) => throw new InvalidOperationException("capability check failed")));
        Assert.Contains("capability check failed", ex.Message);
        // Validation happens before activation: the previous pair is byte-identical.
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(oldFfmpeg));
        Assert.Equal(new byte[] { 2, 2 }, File.ReadAllBytes(oldFfprobe));
        AssertNoScratchFolders(target);
    }

    [Fact]
    public async Task PartialActivationFailureRollsBack()
    {
        var (folder, descriptor) = MakeBundle(new byte[] { 10 }, new byte[] { 20 });
        var target = Path.Combine(folder, "media");
        Directory.CreateDirectory(target);
        var oldFfmpeg = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"));
        File.WriteAllBytes(oldFfmpeg, new byte[] { 1, 1, 1 });
        // A directory squatting on the second tool's path forces the second
        // activation move to fail after the first tool was already replaced.
        var blocker = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe"));
        Directory.CreateDirectory(blocker);
        var error = await Record.ExceptionAsync(() =>
            MediaBundleInstaller.Install(descriptor, DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None, SkipValidation));
        Assert.NotNull(error);
        // Rollback: the first tool's previous bytes are restored, the squatter
        // directory is untouched, and no scratch folders leak.
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(oldFfmpeg));
        Assert.True(Directory.Exists(blocker));
        AssertNoScratchFolders(target);
    }

    [Fact]
    public async Task DefaultInstallValidatesRealCapability()
    {
        // No injected validator: the production path runs the real staged
        // encoding check, so bytes that are not a working ffmpeg never activate.
        var (folder, descriptor) = MakeBundle(new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 });
        var target = Path.Combine(folder, "media");
        var error = await Record.ExceptionAsync(() =>
            MediaBundleInstaller.Install(descriptor, DistributionManifest.CurrentRid(), Unit, target, _ => { }, CancellationToken.None));
        Assert.NotNull(error);
        Assert.False(File.Exists(Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"))));
        Assert.False(File.Exists(Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe"))));
        AssertNoScratchFolders(target);
    }

    [Fact]
    public async Task ExactPathValidationUsesOnlyTheGivenTools()
    {
        var folder = TempPath("gd-exact-");
        Directory.CreateDirectory(folder);
        var ffmpeg = Path.Combine(folder, MediaBundleInstaller.ToolFileName("ffmpeg"));
        var ffprobe = Path.Combine(folder, MediaBundleInstaller.ToolFileName("ffprobe"));
        File.WriteAllBytes(ffmpeg, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(ffprobe, new byte[] { 4, 5, 6 });
        // A missing staged tool fails with words before anything runs.
        var missing = await Assert.ThrowsAsync<MediaFailureException>(() =>
            MediaTools.EnsureEncoding(MediaProfile.Default, false, Path.Combine(folder, "nope"), ffprobe, CancellationToken.None));
        Assert.Contains("missing", missing.Message);
        // Fake bytes are never a working ffmpeg — no env override involved.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            MediaTools.EnsureEncoding(MediaProfile.Default, false, ffmpeg, ffprobe, CancellationToken.None));
    }

    [Fact]
    public async Task FolderLockSerializesAcrossHolders()
    {
        var folder = TempPath("gd-lock-");
        var first = await MediaInstaller.AcquireFolderLock(folder, CancellationToken.None);
        try
        {
            // A second holder (another process would see the same) waits and
            // its cancellation token, not a hang, ends the wait.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // timing-under-test: the token, not a hang, must end the serialized lock wait
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await MediaInstaller.AcquireFolderLock(folder, cts.Token));
        }
        finally
        {
            await first.DisposeAsync();
        }
        var second = await MediaInstaller.AcquireFolderLock(folder, CancellationToken.None);
        await second.DisposeAsync();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    [Fact]
    public async Task NonexistentEnvOverrideFailsHonestly()
    {
        var variable = "GAMEDIRECTOR_FFPROBE";
        var previous = Environment.GetEnvironmentVariable(variable);
        var missing = Path.Combine(TempPath("gd-missing-"), "ffprobe");
        try
        {
            Environment.SetEnvironmentVariable(variable, missing);
            MediaTools.InvalidateCache();
            var resolution = MediaTools.Resolve("ffprobe");
            Assert.True(resolution.Broken);
            Assert.Equal(missing, resolution.Path);
            var ex = await Assert.ThrowsAsync<MediaFailureException>(() =>
                MediaTools.EnsureEncoding(MediaProfile.Default, false, CancellationToken.None));
            Assert.Contains(variable, ex.Message);
            Assert.Contains("does not exist", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            MediaTools.InvalidateCache();
        }
    }

    [Fact]
    public async Task FakeToolFailsVerificationHonestly()
    {
        // An override pointing at bytes that are not a real ffmpeg must surface
        // as an error, never as a silent pass or a fallback to another ffmpeg.
        var variable = "GAMEDIRECTOR_FFMPEG";
        var previous = Environment.GetEnvironmentVariable(variable);
        var fake = Path.Combine(TempPath("gd-fake-"),
            OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
            File.WriteAllBytes(fake, new byte[] { 1, 2, 3 });
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable(variable, fake);
            MediaTools.InvalidateCache();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                MediaTools.EnsureEncoding(MediaProfile.Default, false, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            MediaTools.InvalidateCache();
        }
    }

    [Fact]
    public void OptionsAlwaysIncludeUserBundleButNeverIncomplete()
    {
        var manifest = MediaDistributions.Load(BundledManifestPath());
        var rid = DistributionManifest.CurrentRid();
        var options = MediaInstaller.Options(manifest, rid);
        Assert.Contains(options, o => o.Id == manifest.UserBundle.Id);
        Assert.All(options, o => Assert.NotNull(o.License));
        Assert.DoesNotContain(options, o => o.Id.Contains("ffmpeg-only"));
    }
}
