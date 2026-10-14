using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

public class CatalogRestampTests : TempPaths
{
    private (string catalog, string manifest) WritePair(System.Collections.Generic.List<PerformanceEntry> entries, string? stamp = null, int version = 1)
    {
        var dir = TempPath("gd-catalog-");
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "manifest.json");
        DslJson.Save(manifestPath, TestAssets.Manifest());
        var catalogPath = Path.Combine(dir, "performances.json");
        DslJson.Save(catalogPath, new PerformanceCatalog { Version = version, ManifestSha256 = stamp ?? MediaTools.Hash(manifestPath), Performances = entries });
        return (catalogPath, manifestPath);
    }

    private static System.Collections.Generic.List<PerformanceEntry> Curated() => new()
    {
        new() { Actor = "streamer", Clip = "Cheer", Meaning = "curated meaning", UsefulFor = new() { "celebration" },
            Evidence = new() { new() { Kind = "design", Source = "director note" } } },
        new() { Actor = "streamer", Clip = "Idle" }, // unknowns stay unknown, no claims
    };

    [Fact] public void VersionOneIsRejectedWithRestampInstructions()
    {
        var catalog = new PerformanceCatalog { Version = 1, ManifestSha256 = "anything", Performances = Curated() };
        Assert.Contains(CatalogTools.Validate(catalog, TestAssets.Manifest(), "anything"), e => e.Contains("catalog-restamp"));
        Assert.DoesNotContain(CatalogTools.Validate(catalog, TestAssets.Manifest(), "anything"), e => e.Contains("stale"));
        catalog.Version = 99;
        Assert.Contains(CatalogTools.Validate(catalog, TestAssets.Manifest(), "anything"), e => e.Contains("unsupported catalog version"));
    }

    [Fact] public void RestampMigratesAProvenLegacyStampAndPreservesEntries()
    {
        var (catalogPath, manifestPath) = WritePair(Curated());
        var before = DslJson.Load<PerformanceCatalog>(catalogPath);
        var entriesBefore = DslJson.Serialize(before.Performances);
        var errors = CatalogTools.Restamp(catalogPath, manifestPath, force: false);
        Assert.Empty(errors); // binding errors still surface through the result
        var after = DslJson.Load<PerformanceCatalog>(catalogPath);
        Assert.Equal(PerformanceCatalog.CurrentVersion, after.Version);
        Assert.Equal(ManifestIdentity.Of(DslJson.Load<GameDirector.Core.Dsl.CapabilityManifest>(manifestPath)), after.ManifestSha256);
        Assert.Equal(entriesBefore, DslJson.Serialize(after.Performances)); // curated content untouched
        Assert.Empty(CatalogTools.Validate(after, TestAssets.Manifest(), after.ManifestSha256));
    }

    [Fact] public void RestampEditsOnlyTheStampLinesAndPreservesCuratedText()
    {
        var dir = TempPath("gd-catalog-");
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "manifest.json");
        DslJson.Save(manifestPath, TestAssets.Manifest());
        var legacyStamp = MediaTools.Hash(manifestPath);
        // Hand-curated source file in the store's indented layout: raw Chinese,
        // unknown fields, LF endings, trailing newline.
        var original = "{\n  \"version\": 1,\n  \"manifestSha256\": \"" + legacyStamp + "\",\n  \"curatorNote\": \"保留我：导演手记\",\n  \"performances\": [\n    {\n      \"actor\": \"streamer\",\n      \"clip\": \"Cheer\",\n      \"meaning\": \"得意地展示身体\",\n      \"futureField\": {\n        \"nested\": true\n      },\n      \"evidence\": [\n        {\n          \"kind\": \"design\",\n          \"source\": \"director note\"\n        }\n      ]\n    }\n  ]\n}\n";
        var catalogPath = Path.Combine(dir, "performances.json");
        File.WriteAllText(catalogPath, original);
        Assert.Empty(CatalogTools.Restamp(catalogPath, manifestPath, force: false));
        var restamped = File.ReadAllText(catalogPath);
        var expected = original.Replace("\"version\": 1", "\"version\": " + PerformanceCatalog.CurrentVersion).Replace(legacyStamp, ManifestIdentity.Of(TestAssets.Manifest()));
        Assert.Equal(expected, restamped);
        var before = original.Split('\n'); var after = restamped.Split('\n');
        Assert.Equal(before.Length, after.Length);
        var changed = before.Zip(after).Select((pair, i) => (pair, i)).Where(x => x.pair.First != x.pair.Second).ToArray();
        Assert.Equal(2, changed.Length); // exactly the version and manifestSha256 lines
        Assert.Contains("\"version\": " + PerformanceCatalog.CurrentVersion, after[changed[0].i]);
        Assert.Contains("\"manifestSha256\":", after[changed[1].i]);
        Assert.Contains("保留我：导演手记", restamped); // raw UTF-8, never \uXXXX
        Assert.DoesNotContain("\\u", restamped);
        Assert.True(restamped.EndsWith("\n") && !restamped.Contains("\r\n")); // LF style and trailing newline kept
        var node = System.Text.Json.Nodes.JsonNode.Parse(restamped)!; // unknown fields survive
        Assert.Equal("保留我：导演手记", node["curatorNote"]!.GetValue<string>());
        Assert.True(node["performances"]![0]!["futureField"]!["nested"]!.GetValue<bool>());
        Assert.Equal(PerformanceCatalog.CurrentVersion, DslJson.Load<PerformanceCatalog>(catalogPath).Version);
    }

    [Fact] public void RestampRefusesAnUnprovenManifestWithoutForce()
    {
        var (catalogPath, manifestPath) = WritePair(Curated(), stamp: new string('0', 64));
        var bytes = File.ReadAllBytes(catalogPath);
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogTools.Restamp(catalogPath, manifestPath, force: false));
        Assert.Contains("re-scouted", ex.Message);
        Assert.Contains("--force", ex.Message);
        Assert.Equal(bytes, File.ReadAllBytes(catalogPath)); // refusal never touches the file
    }

    [Fact] public void ForceRestampsAnUnprovenManifest()
    {
        var (catalogPath, manifestPath) = WritePair(Curated(), stamp: new string('0', 64));
        Assert.Empty(CatalogTools.Restamp(catalogPath, manifestPath, force: true));
        var after = DslJson.Load<PerformanceCatalog>(catalogPath);
        Assert.Equal(PerformanceCatalog.CurrentVersion, after.Version);
        Assert.Equal(ManifestIdentity.Of(TestAssets.Manifest()), after.ManifestSha256);
    }

    [Fact] public void RestampIsAnIdempotentNoOpOnACurrentCatalog()
    {
        var (catalogPath, manifestPath) = WritePair(Curated());
        CatalogTools.Restamp(catalogPath, manifestPath, force: false);
        var bytes = File.ReadAllBytes(catalogPath);
        var mtime = File.GetLastWriteTimeUtc(catalogPath);
        Assert.Empty(CatalogTools.Restamp(catalogPath, manifestPath, force: false)); // already current: no rewrite
        Assert.Equal(bytes, File.ReadAllBytes(catalogPath));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(catalogPath));
    }

    [Fact] public void RestampRefusesUnknownVersionsWithoutForce()
    {
        var (catalogPath, manifestPath) = WritePair(Curated(), version: 99);
        Assert.Throws<InvalidOperationException>(() => CatalogTools.Restamp(catalogPath, manifestPath, force: false));
    }
}
