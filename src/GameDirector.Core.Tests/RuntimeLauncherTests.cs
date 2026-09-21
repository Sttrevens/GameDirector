using System.Security.Cryptography;
using System.Text.Json;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>CLI launching: sibling native apps run with args/stdio/exit codes
/// preserved, cancellation tears the child down, and a distribution that lacks
/// the app produces an actionable error instead of a silent fallback.</summary>
[Collection("DistributionEnv")]
public class RuntimeLauncherTests
{
    private static (string exe, string[] exitArgs, string[] sleepArgs) Shell()
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", new[] { "/c", "exit 3" }, new[] { "/c", "ping -n 30 127.0.0.1 >nul" });
        return ("/bin/sh", new[] { "-c", "exit 3" }, new[] { "-c", "sleep 30" });
    }

    [Fact]
    public async Task ExitCodePassesThroughVerbatim()
    {
        var (exe, exitArgs, _) = Shell();
        Assert.Equal(3, await RuntimeLauncher.Run(exe, exitArgs, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationKillsTheChild()
    {
        var (exe, _, sleepArgs) = Shell();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        await RuntimeLauncher.Run(exe, sleepArgs, cts.Token);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "child was not torn down on cancellation");
    }

    private const string ExecutableName = "gamedirector-workbench";
    private static readonly byte[] ExecutableBytes = { 9, 8, 7, 6 };

    private static string MakeDistribution(Dictionary<string, string> sha256, string[]? executables, out string executable)
    {
        var folder = Path.Combine(Path.GetTempPath(), "gd-dist-" + Guid.NewGuid().ToString("N"));
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(repo, "tools", "distribution.json"), Path.Combine(folder, "distribution.json"));
        var appFolder = Path.Combine(folder, "workbench");
        Directory.CreateDirectory(appFolder);
        executable = Path.Combine(appFolder, ExecutableName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.WriteAllBytes(executable, ExecutableBytes);
        var runtime = new
        {
            product = "GameDirector",
            app = "workbench",
            version = "1.0.0",
            unityPackageVersion = "1.0.0",
            rid = DistributionManifest.CurrentRid(),
            protocolVersion = 1,
            executables = executables ?? new[] { Path.GetFileName(executable) },
            sha256,
        };
        File.WriteAllText(Path.Combine(appFolder, "gamedirector.runtime.json"), JsonSerializer.Serialize(runtime));
        return folder;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void WithDistribution(string folder, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, folder);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, previous);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MatchingExecutableHashPassesTheGate()
    {
        var folder = MakeDistribution(new Dictionary<string, string> { [ExecutableName + (OperatingSystem.IsWindows() ? ".exe" : "")] = Sha(ExecutableBytes) }, null, out var executable);
        WithDistribution(folder, () => Assert.Equal(executable, RuntimeLauncher.ResolveSibling("workbench")));
    }

    [Fact]
    public void TamperedExecutableFailsIntegrityGate()
    {
        var folder = MakeDistribution(new Dictionary<string, string> { [ExecutableName + (OperatingSystem.IsWindows() ? ".exe" : "")] = new string('0', 64) }, null, out _);
        WithDistribution(folder, () =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("workbench"));
            Assert.Contains("integrity", ex.Message);
            Assert.Contains("modified or corrupted", ex.Message);
        });
    }

    [Fact]
    public void UndeclaredExecutableFailsIntegrityGate()
    {
        var folder = MakeDistribution(new Dictionary<string, string> { ["some-other-file.dll"] = new string('a', 64) }, null, out _);
        WithDistribution(folder, () =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("workbench"));
            Assert.Contains("not declared", ex.Message);
        });
    }

    [Fact]
    public void UnsafeDeclaredPathIsRejectedBeforeLaunch()
    {
        var folder = MakeDistribution(new Dictionary<string, string> { ["../outside.txt"] = new string('a', 64) }, null, out _);
        WithDistribution(folder, () =>
            Assert.Throws<InvalidDataException>(() => RuntimeLauncher.ResolveSibling("workbench")));
    }

    [Fact]
    public void UnsafeExecutablePathIsRejectedBeforeLaunch()
    {
        var folder = MakeDistribution(new Dictionary<string, string>(), new[] { "../../etc/tool" }, out _);
        WithDistribution(folder, () =>
            Assert.Throws<InvalidDataException>(() => RuntimeLauncher.ResolveSibling("workbench")));
    }

    [Fact]
    public void MissingSiblingInDistributionIsActionable()
    {
        var previous = Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable);
        var folder = Path.Combine(Path.GetTempPath(), "gd-dist-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            File.Copy(Path.Combine(repo, "tools", "distribution.json"), Path.Combine(folder, "distribution.json"));
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, folder);
            var ex = Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("workbench"));
            Assert.Contains("workbench", ex.Message);
            Assert.Contains(folder, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, previous);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void OutsideDistributionIsActionable()
    {
        if (Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable) != null) return; // env-dependent
        if (DistributionManifest.FindRoot() != null) return; // running inside a staged distribution
        var ex = Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("mcp"));
        Assert.Contains("distribution", ex.Message);
    }
}
