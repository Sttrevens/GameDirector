using System.ComponentModel;
using System.Diagnostics;
using GameDirector.Client;
using Xunit;

public class OutputPolicyTests : TempPaths
{
    [Fact] public void OutputCannotEnterAnEngineOrAnAliasOfIt()
    {
        var root = TempPath("gd-path-");
        Directory.CreateDirectory(Path.Combine(root, "game", "Assets")); Directory.CreateDirectory(Path.Combine(root, "game", "ProjectSettings"));
        try
        {
            Assert.Throws<ArgumentException>(() => OutputPolicy.RequireOutput(Path.Combine(root, "game", "renders")));
            TestDirectoryLinks.Create(Path.Combine(root, "alias"), Path.Combine(root, "game"));
            Assert.Throws<ArgumentException>(() => OutputPolicy.RequireOutput(Path.Combine(root, "alias", "renders")));
            Directory.CreateDirectory(Path.Combine(root, "store"));
            if (OperatingSystem.IsWindows())
                TestDirectoryLinks.Create(Path.Combine(root, "store", ".recording"), Path.Combine(root, "game"));
            else
                File.CreateSymbolicLink(Path.Combine(root, "store", ".recording"), Path.Combine(root, "game", "new-file"));
            Assert.Throws<StoreIntegrityException>(() => TakeJobs.Lock(Path.Combine(root, "store")));
            Assert.False(File.Exists(Path.Combine(root, "game", "new-file")));
            Assert.False(Directory.Exists(Path.Combine(root, "game", "renders")));
        }
        finally
        {
            // Remove aliases before their targets; Windows junction removal
            // cannot be delegated to recursive deletion after target removal.
            foreach (var alias in new[] { Path.Combine(root, "store", ".recording"), Path.Combine(root, "alias") })
                if (new DirectoryInfo(alias).LinkTarget != null)
                {
                    if (Directory.Exists(alias)) Directory.Delete(alias); else File.Delete(alias);
                }
            Directory.Delete(root, true);
        }
    }
    [Fact] public void KnownCustomSourceIsProtectedAndSiblingOutputIsAllowed()
    {
        var root = TempPath("gd-custom-");
        Directory.CreateDirectory(Path.Combine(root, "game"));
        try
        {
            Assert.Throws<ArgumentException>(() => OutputPolicy.RequireOutput(Path.Combine(root,"game","new"),Path.Combine(root,"game")));
            Assert.NotNull(OutputPolicy.RequireOutput(Path.Combine(root,"game-renders"),Path.Combine(root,"game")));
            TestDirectoryLinks.Create(Path.Combine(root,"frames"),Path.Combine(root,"game"));
            Assert.Throws<StoreIntegrityException>(() => OutputPolicy.Child(Path.Combine(root,"frames"), "..", "escaped"));
        }
        finally
        {
            var alias = Path.Combine(root, "frames");
            if (new DirectoryInfo(alias).LinkTarget != null) Directory.Delete(alias);
            Directory.Delete(root,true);
        }
    }
    [Fact] public void HomeProfileIsAContainerNeverAProject()
    {
        var root = TempPath("gd-home-");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "work", "captures"));
        File.WriteAllText(Path.Combine(profile, "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(profile, ".git"));
        try
        {
            // A stray package.json or dotfiles .git in the profile must not poison outputs below it.
            Assert.NotNull(OutputPolicy.RequireOutput(Path.Combine(profile, "work", "captures", "out"), null, profile));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void NodeProjectBelowTheProfileStillRejects()
    {
        var root = TempPath("gd-node-");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "work", "app"));
        File.WriteAllText(Path.Combine(profile, "work", "app", "package.json"), "{}");
        try { Assert.Throws<ArgumentException>(() => OutputPolicy.RequireOutput(Path.Combine(profile, "work", "app", "renders"), null, profile)); }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void UnityProjectBelowTheProfileStillRejects()
    {
        var root = TempPath("gd-unity-");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "work", "game", "Assets"));
        Directory.CreateDirectory(Path.Combine(profile, "work", "game", "ProjectSettings"));
        try { Assert.Throws<ArgumentException>(() => OutputPolicy.RequireOutput(Path.Combine(profile, "work", "game", "renders"), null, profile)); }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void RepositoryBelowTheProfileStillRequiresIgnoredOutputs()
    {
        if (!GitAvailable()) return; // git-dependent
        var root = TempPath("gd-repo-");
        var profile = Path.Combine(root, "profile");
        var repository = Path.Combine(profile, "work", "repo");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-q");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "ignored/\n");
        try
        {
            Assert.Throws<ArgumentException>(() => OutputPolicy.RequireOutput(Path.Combine(repository, "kept", "out"), null, profile));
            Assert.NotNull(OutputPolicy.RequireOutput(Path.Combine(repository, "ignored", "out"), null, profile));
        }
        finally { Directory.Delete(root, true); }
    }
    private static bool GitAvailable()
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(5000)) { process.Kill(); return false; }
            return process.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }
    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("Timed out running git " + arguments[0]); }
        if (process.ExitCode != 0) throw new IOException("git " + arguments[0] + " failed: " + process.StandardError.ReadToEnd());
    }
}
