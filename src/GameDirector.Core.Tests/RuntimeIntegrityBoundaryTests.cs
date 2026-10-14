using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

[Collection("DistributionEnv")]
public class RuntimeIntegrityBoundaryTests : TempPaths
{
    [Fact]
    public void UnchangedNativeHostDoesNotHideModifiedManagedProgram()
    {
        var root = TempPath("gd-runtime-boundary-");
        var folder = Path.Combine(root, "bin");
        Directory.CreateDirectory(folder);
        var previous = Environment.GetEnvironmentVariable(DistributionManifest.RootOverrideVariable);
        try
        {
            var rid = DistributionManifest.CurrentRid();
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            var executable = "test-workbench" + extension;
            File.WriteAllText(Path.Combine(root, "distribution.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, product = WorkbenchService.Product, protocolVersion = WorkbenchService.ProtocolVersion,
                supportedPlatforms = new[] { rid },
                platforms = new Dictionary<string, object> { [rid] = new { executableExtension = extension } },
                runtimeFolder = "bin", apps = new { workbench = new { executable = "test-workbench" } },
                unity = new { bundledRuntimeParent = "Tools~" }
            }));
            string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(folder, executable), "native-host");
            File.WriteAllText(Path.Combine(folder, "test-workbench.dll"), "modified-code");
            File.WriteAllText(Path.Combine(folder, "gamedirector.runtime.json"), JsonSerializer.Serialize(new {
                product = WorkbenchService.Product, version = "1", rid, app = "shared", protocolVersion = WorkbenchService.ProtocolVersion,
                apps = new Dictionary<string, string> { ["workbench"] = executable },
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
