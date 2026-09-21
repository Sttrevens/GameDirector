using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

[Collection("DistributionEnv")]
public class RuntimeIntegrityBoundaryTests
{
    [Fact]
    public void UnchangedNativeHostDoesNotHideModifiedManagedProgram()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-runtime-boundary-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "workbench");
        Directory.CreateDirectory(folder);
        var previous = Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable);
        try
        {
            var rid = DistributionManifest.CurrentRid();
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            var executable = "test-workbench" + extension;
            File.WriteAllText(Path.Combine(root, "distribution.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, product = "GameDirector", protocolVersion = 1,
                supportedPlatforms = new[] { rid },
                platforms = new Dictionary<string, object> { [rid] = new { executableExtension = extension } },
                apps = new { workbench = new { folder = "workbench", executable = "test-workbench" } }
            }));
            string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(folder, executable), "native-host");
            File.WriteAllText(Path.Combine(folder, "test-workbench.dll"), "modified-code");
            File.WriteAllText(Path.Combine(folder, "gamedirector.runtime.json"), JsonSerializer.Serialize(new {
                product = "GameDirector", version = "1", rid, app = "workbench", protocolVersion = 1,
                sha256 = new Dictionary<string, string> { [executable] = Hash("native-host"), ["test-workbench.dll"] = Hash("original-code") }
            }));
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, root);
            var error = Assert.Throws<InvalidOperationException>(() => RuntimeLauncher.ResolveSibling("workbench"));
            Assert.Contains("test-workbench.dll", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DistributionManifest.RootOverrideVariable, previous);
            Directory.Delete(root, true);
        }
    }
}
