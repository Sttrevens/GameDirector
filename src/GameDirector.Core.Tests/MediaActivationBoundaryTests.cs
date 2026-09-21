using System.Security.Cryptography;
using System.Text.Json;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

public class MediaActivationBoundaryTests
{
    [Fact]
    public async Task FailureAfterMovingPreviousFileAsideRestoresBothTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-activation-boundary-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(target);
        try
        {
            var tools = new[] { "ffmpeg", "ffprobe" };
            var files = new Dictionary<string, object>();
            foreach (var tool in tools)
            {
                var input = Path.Combine(root, tool + "-input");
                File.WriteAllText(input, "new-" + tool);
                files[tool] = new { path = Path.GetFileName(input), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant() };
                File.WriteAllText(Path.Combine(target, MediaBundleInstaller.ToolFileName(tool)), "old-" + tool);
            }
            var descriptor = Path.Combine(root, "bundle.json");
            File.WriteAllText(descriptor, JsonSerializer.Serialize(new { schemaVersion = 1, files }));
            await Assert.ThrowsAnyAsync<IOException>(() => MediaBundleInstaller.Install(
                descriptor, DistributionManifest.CurrentRid(), new MediaDistributions.UnitLayout { Tools = tools },
                target, _ => { }, CancellationToken.None,
                (staged, _) =>
                {
                    // Model a staged file disappearing after validation, before
                    // activation. The second old file has already moved aside
                    // when its replacement fails to move into place.
                    File.Delete(staged["ffprobe"]);
                    return Task.CompletedTask;
                }));
            foreach (var tool in tools)
                Assert.Equal("old-" + tool, File.ReadAllText(Path.Combine(target, MediaBundleInstaller.ToolFileName(tool))));
            Assert.Empty(Directory.GetDirectories(target, ".backup-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
