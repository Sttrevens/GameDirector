using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>
/// Authored documents (timelines, edit plans, film plans, catalogs, manifest
/// files) are parsed strictly: an unknown field fails at the field, a missing
/// required field names itself. The bridge wire protocol and stored receipts
/// stay tolerant so version skew never breaks takes.
/// </summary>
public sealed class AuthoredInputTests : TempPaths
{
    [Fact]
    public void Unknown_property_at_depth_names_its_json_path()
    {
        const string json = """{"version":"0.1","id":"t","cues":[{"t":0,"type":"camera.shot","shot":{"type":"lockoff","subject":"hero","durationSecond":3}}]}""";
        var ex = Assert.Throws<AuthoredInputException>(() => DslJson.DeserializeAuthored<TimelineAsset>(json));
        Assert.Equal("cues[0].shot.durationSecond: unknown property", ex.Message);
    }

    [Fact]
    public void Unknown_top_level_property_and_type_mismatch_name_their_paths()
    {
        var ex = Assert.Throws<AuthoredInputException>(() =>
            DslJson.DeserializeAuthored<TimelineAsset>("""{"version":"0.1","id":"t","cues":[],"durration":3}"""));
        Assert.Equal("durration: unknown property", ex.Message);
        ex = Assert.Throws<AuthoredInputException>(() =>
            DslJson.DeserializeAuthored<TimelineAsset>("""{"version":"0.1","id":"t","cues":[{"t":"zero"}]}"""));
        Assert.StartsWith("cues[0].t: ", ex.Message);
    }

    [Fact]
    public void Missing_required_fields_fail_before_any_default_is_applied()
    {
        // A file without version must not silently become "0.1".
        Assert.Equal("version: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<TimelineAsset>("""{"id":"t","cues":[]}""")).Message);
        Assert.Equal("id: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<TimelineAsset>("""{"version":"0.1","cues":[]}""")).Message);
        Assert.Equal("cues: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<TimelineAsset>("""{"version":"0.1","id":"t"}""")).Message);
        Assert.Equal("version: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<EditPlan>("""{"frameRate":24}""")).Message);
        Assert.Equal("version: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<FilmPlan>("""{"title":"t","scenes":[]}""")).Message);
        Assert.Equal("manifestSha256: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<PerformanceCatalog>("""{"version":2,"performances":[]}""")).Message);
        Assert.Equal("manifestVersion: required", Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<CapabilityManifest>("""{"game":"g"}""")).Message);
    }

    [Fact]
    public void Strict_load_is_a_json_exception_so_repair_loops_keep_catching_it()
    {
        Assert.IsAssignableFrom<JsonException>(Assert.Throws<AuthoredInputException>(
            () => DslJson.DeserializeAuthored<FilmPlan>("""{"title":"t"}""")));
    }

    [Fact]
    public void LoadAuthored_names_the_file_and_the_field()
    {
        var path = TempPath("gd-authored-", ".json");
        File.WriteAllText(path, """{"version":"0.1","id":"t","cues":[{"t":0,"type":"marker","label":"x","labell":"y"}]}""");
        try
        {
            var ex = Assert.Throws<AuthoredInputException>(() => DslJson.LoadAuthored<TimelineAsset>(path));
            Assert.Contains(path, ex.Message);
            Assert.Contains("cues[0].labell: unknown property", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tolerant_path_still_accepts_unknown_properties_and_missing_version()
    {
        // Bridge payloads and stored receipts must survive version skew: an
        // unknown property is ignored and a missing version keeps its default.
        var timeline = DslJson.Deserialize<TimelineAsset>(
            """{"id":"t","cues":[{"t":0,"type":"marker","label":"x","futureField":1}],"futureTop":{"a":1}}""");
        Assert.Equal("0.1", timeline.Version);
        Assert.Single(timeline.Cues);
        var receipt = DslJson.Deserialize<TakeReceipt>(
            """{"takeId":"k","timelineId":"t","state":"Completed","frameCount":1,"capturedFrames":1,"frameRate":24,"width":64,"height":64,"futureWireField":"skew"}""");
        Assert.Equal("k", receipt.TakeId);
    }
}
