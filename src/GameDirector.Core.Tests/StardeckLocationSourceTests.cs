using System.Globalization;
using System.Text.RegularExpressions;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>Public Stardeck copies must not drift in bytes or location geometry.</summary>
public sealed class StardeckLocationSourceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameDirector.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root containing GameDirector.sln");
    }

    [Fact]
    public void Both_asset_copies_are_byte_identical()
    {
        var root = RepoRoot();
        foreach (var name in new[] { "RobotExpressive.glb", "space_ship_hallway.glb" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, "adapters/stardeck/assets", name)),
                File.ReadAllBytes(Path.Combine(root, "samples/godot/stardeck/assets", name)));
    }

    [Fact]
    public void Stage_locations_match_manifest_and_adapter_keeps_no_location_table()
    {
        var root = RepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "adapters/stardeck/adapter.js"));
        Assert.DoesNotMatch(new Regex(@"\[""[^""]+""\]\s*=\s*new (?:THREE\.)?Vector3\("), source);
        Assert.Contains("ctx.manifest.locations.find", source);
        Assert.Contains(".position", source);
        // The Godot sample's metadata-only markers must describe the same stage
        // as the Three adapter's manifest; a changed coordinate fails this test.
        var manifest = DslJson.LoadAuthored<CapabilityManifest>(Path.Combine(root, "adapters/stardeck/manifest.json"));
        var scene = File.ReadAllText(Path.Combine(root, "samples/godot/stardeck/stage.tscn"));
        var markers = Regex.Matches(scene,
            @"\[node name=""([^""]+)"" type=""Marker3D"" parent=""\.""\]\s+position = Vector3\(([^)]+)\)\s+metadata/gamedirector_location = ""([^""]+)""");
        Assert.Equal(manifest.Locations.Count, markers.Count);
        foreach (Match marker in markers)
        {
            Assert.Equal(marker.Groups[1].Value, marker.Groups[3].Value);
            var location = manifest.FindLocation(marker.Groups[3].Value);
            Assert.NotNull(location);
            var position = marker.Groups[2].Value.Split(',').Select(p => float.Parse(p, CultureInfo.InvariantCulture));
            Assert.Equal(location.Position, position);
        }
    }
}
