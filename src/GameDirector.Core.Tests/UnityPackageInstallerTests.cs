using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>Contract tests for the .NET Unity package installer. These mirror
/// tools/test_unity_installer.py (the Python installer must keep passing the
/// same semantics) plus the safety cases: conflicts abort before any write,
/// receipts with escaping paths are rejected, symlinked destinations refuse.</summary>
[Collection("DistributionEnv")]
public class UnityPackageInstallerTests
{
    private static (string root, string project, string source) Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-install-test-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        var source = Path.Combine(root, "package");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "package.json"), "{\"name\":\"com.gamedirector.test\",\"version\":\"1\"}");
        File.WriteAllText(Path.Combine(source, "owned.cs"), "v1");
        return (root, project, source);
    }

    [Fact]
    public void PlanOnlyPerformsNoWrites()
    {
        var (_, project, source) = Fixture();
        var plan = UnityPackageInstaller.Install(source, project, apply: false);
        Assert.Equal("plan", plan.Mode);
        Assert.Equal(3, plan.Writes); // package.json + owned.cs + receipt
        Assert.False(Directory.Exists(Path.Combine(project, "Packages")));
    }

    [Fact]
    public void RepeatedIdenticalInstallWritesNothing()
    {
        var (_, project, source) = Fixture();
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        Assert.Equal("installed", first.Mode);
        var target = first.Destination;
        var before = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p).Ticks));
        var second = UnityPackageInstaller.Install(source, project, apply: true);
        Assert.Equal(0, second.Writes);
        var after = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p).Ticks));
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, (bytes, ticks)) in before)
        {
            Assert.True(after.TryGetValue(path, out var now));
            Assert.Equal(ticks, now.Ticks);
            Assert.Equal(bytes, now.Item1);
        }
    }

    [Fact]
    public void LocalEditConflictAbortsBeforeAnyWrite()
    {
        var (_, project, source) = Fixture();
        File.WriteAllText(Path.Combine(source, "second.cs"), "s1");
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        var target = first.Destination;
        // Locally edit one file; upgrade both files upstream.
        File.WriteAllText(Path.Combine(target, "owned.cs"), "user edit");
        File.WriteAllText(Path.Combine(source, "owned.cs"), "v2");
        File.WriteAllText(Path.Combine(source, "second.cs"), "s2");
        var secondMtime = File.GetLastWriteTimeUtc(Path.Combine(target, "second.cs")).Ticks;

        var ex = Assert.Throws<UnityPackageInstaller.InstallConflictException>(
            () => UnityPackageInstaller.Install(source, project, apply: true));
        Assert.Contains("owned.cs", ex.Message);
        // The non-conflicting upgrade must NOT have been written.
        Assert.Equal("user edit", File.ReadAllText(Path.Combine(target, "owned.cs")));
        Assert.Equal("s1", File.ReadAllText(Path.Combine(target, "second.cs")));
        Assert.Equal(secondMtime, File.GetLastWriteTimeUtc(Path.Combine(target, "second.cs")).Ticks);
    }

    [Fact]
    public void UpgradeAfterRestoringOwnedFileSucceeds()
    {
        var (_, project, source) = Fixture();
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        var target = first.Destination;
        File.WriteAllText(Path.Combine(target, "owned.cs"), "user edit");
        File.WriteAllText(Path.Combine(source, "owned.cs"), "v2");
        Assert.Throws<UnityPackageInstaller.InstallConflictException>(
            () => UnityPackageInstaller.Install(source, project, apply: true));
        File.WriteAllText(Path.Combine(target, "owned.cs"), "v1"); // user reverts
        var plan = UnityPackageInstaller.Install(source, project, apply: true);
        Assert.Equal(2, plan.Writes); // owned.cs + receipt
        Assert.Equal("v2", File.ReadAllText(Path.Combine(target, "owned.cs")));
    }

    [Fact]
    public void OwnedRemovalDeletesUntouchedFileOnly()
    {
        var (_, project, source) = Fixture();
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        File.Delete(Path.Combine(source, "owned.cs"));
        var plan = UnityPackageInstaller.Install(source, project, apply: true);
        Assert.Equal(new[] { "owned.cs" }, plan.RemovedOwnedFiles);
        Assert.False(File.Exists(Path.Combine(first.Destination, "owned.cs")));
    }

    [Fact]
    public void LocallyEditedRemovalIsPreserved()
    {
        var (_, project, source) = Fixture();
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        File.WriteAllText(Path.Combine(first.Destination, "owned.cs"), "user edit");
        File.Delete(Path.Combine(source, "owned.cs"));
        Assert.Throws<UnityPackageInstaller.InstallConflictException>(
            () => UnityPackageInstaller.Install(source, project, apply: true));
        Assert.Equal("user edit", File.ReadAllText(Path.Combine(first.Destination, "owned.cs")));
    }

    [Fact]
    public void ReceiptPathTraversalIsRejected()
    {
        var (_, project, source) = Fixture();
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        var receipt = Path.Combine(first.Destination, ".gamedirector-install.json");
        File.WriteAllText(receipt, "{\"package\":\"com.gamedirector.test\",\"version\":\"1\",\"files\":{\"../evil.txt\":\""
            + new string('0', 64) + "\"}}");
        Assert.Throws<InvalidDataException>(() => UnityPackageInstaller.Install(source, project, apply: true));
        Assert.False(File.Exists(Path.Combine(project, "Packages", "evil.txt")));
    }

    [Fact]
    public void MalformedReceiptIsAnHonestError()
    {
        var (_, project, source) = Fixture();
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        File.WriteAllText(Path.Combine(first.Destination, ".gamedirector-install.json"), "not json {");
        var ex = Assert.Throws<InvalidDataException>(() => UnityPackageInstaller.Install(source, project, apply: true));
        Assert.Contains("receipt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SymlinkedPackageDestinationIsRefused()
    {
        var (_, project, source) = Fixture();
        var elsewhere = Path.Combine(Path.GetTempPath(), "gd-link-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        TestDirectoryLinks.Create(Path.Combine(project, "Packages", "com.gamedirector.test"), elsewhere);
        Assert.Throws<ArgumentException>(() => UnityPackageInstaller.Install(source, project, apply: true));
        Assert.False(File.Exists(Path.Combine(elsewhere, "owned.cs")));
    }

    [Fact]
    public void SymlinkedAncestorInsideTargetIsRefused()
    {
        var (_, project, source) = Fixture();
        Directory.CreateDirectory(Path.Combine(source, "Editor"));
        File.WriteAllText(Path.Combine(source, "Editor", "tool.cs"), "v1");
        var first = UnityPackageInstaller.Install(source, project, apply: true);
        // Replace the installed Editor folder with a link and try to write through it.
        var linked = Path.Combine(first.Destination, "Editor");
        var elsewhere = Path.Combine(Path.GetTempPath(), "gd-link-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(linked, recursive: true);
        TestDirectoryLinks.Create(linked, elsewhere);
        File.WriteAllText(Path.Combine(source, "Editor", "tool.cs"), "v2");
        Assert.ThrowsAny<Exception>(() => UnityPackageInstaller.Install(source, project, apply: true));
        // Nothing may have been written through the link.
        Assert.False(File.Exists(Path.Combine(elsewhere, "tool.cs")));
    }

    [Fact]
    public void NonProjectRootsAndBadIdentitiesAreRejected()
    {
        var (root, project, source) = Fixture();
        Assert.Throws<ArgumentException>(() => UnityPackageInstaller.Install(source, root, apply: false));
        File.WriteAllText(Path.Combine(source, "package.json"), "{\"name\":\"com.other.pkg\",\"version\":\"1\"}");
        Assert.Throws<ArgumentException>(() => UnityPackageInstaller.Install(source, project, apply: false));
    }

    [Fact]
    public void DefaultSourceWithoutDistributionIsActionable()
    {
        if (Environment.GetEnvironmentVariable("GAMEDIRECTOR_DISTRIBUTION_ROOT") != null) return; // env-dependent
        var ex = Assert.Throws<InvalidOperationException>(UnityPackageInstaller.DefaultSource);
        Assert.Contains("--source", ex.Message);
    }
}
