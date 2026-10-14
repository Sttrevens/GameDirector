using System.Text.Json;
using GameDirector.Client;
using Xunit;

public sealed class InstallerBoundaryTests : TempPaths
{
    [Fact]
    public void RemovingOwnedFileCannotFollowAReplacedDirectory()
    {
        var root = TempPath("gd-install-boundary-");
        var source = Path.Combine(root, "package");
        var project = Path.Combine(root, "project");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(project, "Packages", "com.gamedirector.test", "nested");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "package.json"), JsonSerializer.Serialize(new { name = "com.gamedirector.test", version = "1" }));
        File.WriteAllText(Path.Combine(source, "nested", "owned.txt"), "owned-v1");
        GameDirector.Core.Tests.InstallerRuntimeFixture.Write(source, "1");
        try
        {
            UnityPackageInstaller.Install(source, project, true);
            File.Delete(Path.Combine(link, "owned.txt"));
            Directory.Delete(link);
            File.WriteAllText(Path.Combine(outside, "owned.txt"), "owned-v1");
            TestDirectoryLinks.Create(link, outside);
            File.Delete(Path.Combine(source, "nested", "owned.txt"));
            var error = Record.Exception(() => UnityPackageInstaller.Install(source, project, true));
            Assert.NotNull(error);
            Assert.Equal("owned-v1", File.ReadAllText(Path.Combine(outside, "owned.txt")));
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget != null) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SourceDirectoryLinksAreRejectedBeforeAnyProjectWrites()
    {
        var root = TempPath("gd-source-boundary-");
        var source = Path.Combine(root, "package");
        var project = Path.Combine(root, "project");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(source, "nested");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(Path.Combine(source, "package.json"), JsonSerializer.Serialize(new { name = "com.gamedirector.test", version = "1" }));
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-source");
        try
        {
            TestDirectoryLinks.Create(link, outside);
            Assert.NotNull(Record.Exception(() => UnityPackageInstaller.Install(source, project, true)));
            Assert.False(Directory.Exists(Path.Combine(project, "Packages")));
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget != null) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }
}
