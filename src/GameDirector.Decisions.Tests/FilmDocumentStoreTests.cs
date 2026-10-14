using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

/// <summary>Stored-record integrity against the frozen v1 fixture store (produced
/// by the unmodified baseline code; never edited) and the v2 forward contract.</summary>
public class FilmDocumentStoreTests : TempPaths
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-store");

    private string CopyFixtureStore()
    {
        var store = TempPath("gd-v1-store-");
        CopyDirectory(FixtureRoot, store);
        return store;
    }
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(source, target));
    }
    private static Dictionary<string, (byte[] Bytes, long Mtime)> Snapshot(string store) =>
        Directory.GetFiles(store, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(store, f), f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f).Ticks));
    private static void AssertUnchanged(string store, Dictionary<string, (byte[] Bytes, long Mtime)> snapshot)
    {
        foreach (var (file, state) in snapshot)
        {
            var path = Path.Combine(store, file);
            Assert.Equal(state.Bytes, File.ReadAllBytes(path));
            Assert.Equal(state.Mtime, File.GetLastWriteTimeUtc(path).Ticks);
        }
    }
    private static object? Prop(object anon, string name) => anon.GetType().GetProperty(name)?.GetValue(anon);
    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string RevisionFile(string store, int revision) => Path.Combine(store, "documents", "fixture", "film-a", revision.ToString("D12") + ".json");

    [Fact]
    public void V1FixtureReadsAndListsWithoutModifyingTheStore()
    {
        var store = CopyFixtureStore();
        var snapshot = Snapshot(store);
        using var studio = new Studio(store);
        var latest = studio.Documents.Read("fixture", "film-a");
        Assert.Equal(2, latest.Revision);
        Assert.Equal("第二版", latest.Film!.Title);
        var cues = latest.Film.Scenes[0].Performance;
        Assert.Equal(6, cues.Count);
        Assert.Equal("actor.anim", cues[0].Type);
        Assert.Equal("idle", cues[0].Clip);
        Assert.Equal(0.2f, cues[0].Fade);
        Assert.Equal("beat — 高潮", cues[4].Label);
        Assert.Equal("你好，世界\n第二行", latest.Film.Subtitles[0].Text);
        Assert.Equal(0.8, latest.Film.Audio[0].Volume);
        var first = studio.Documents.Read("fixture", "film-a", 1);
        Assert.Equal(1, first.Revision);
        Assert.Equal("导演剪辑 \"v1\" <fixture> & co", first.Film!.Title);
        Assert.Equal(0.2f, first.Film.Scenes[0].Performance[0].Fade); // fade defaults survive
        var entry = Assert.Single(studio.Documents.List("fixture"));
        Assert.Equal("film-a", Prop(entry, "Id"));
        Assert.Equal(2L, Prop(entry, "Revision"));
        Assert.Equal("第二版", Prop(entry, "title"));
        AssertUnchanged(store, snapshot); // reads never rewrite history
    }

    [Fact]
    public void NewSaveWritesVersion2AlongsideUntouchedV1Revisions()
    {
        var store = CopyFixtureStore();
        var snapshot = Snapshot(store);
        using var studio = new Studio(store);
        var film = studio.Documents.Read("fixture", "film-a").Film!;
        film.Title = "第三版";
        var saved = studio.Documents.Save("fixture", "film-a", new SaveFilmDocument(2, "rev-3", film));
        Assert.Equal(3, saved.Revision);
        var node = JsonNode.Parse(File.ReadAllText(RevisionFile(store, 3)))!;
        Assert.Equal(FilmDocument.CurrentVersion, node["version"]!.GetValue<int>());
        Assert.Equal(ContentIdentity.OfNode(node["film"]), node["contentHash"]!.GetValue<string>());
        // The mixed v1/v2 chain reads back; original revision bytes were preserved.
        Assert.Equal("导演剪辑 \"v1\" <fixture> & co", studio.Documents.Read("fixture", "film-a", 1).Film!.Title);
        Assert.Equal("第二版", studio.Documents.Read("fixture", "film-a", 2).Film!.Title);
        Assert.Equal("第三版", studio.Documents.Read("fixture", "film-a").Film!.Title);
        Assert.Single(studio.Documents.List("fixture"));
        AssertUnchanged(store, snapshot);
    }

    [Fact]
    public void RetriedSaveAcceptsIdenticalContentAgainstALegacyReceipt()
    {
        var store = CopyFixtureStore();
        var snapshot = Snapshot(store);
        using var studio = new Studio(store);
        // The v1 prior stores a legacy hash; retry dedupe compares semantic content instead.
        var film = studio.Documents.Read("fixture", "film-a", 2).Film!;
        var prior = studio.Documents.Save("fixture", "film-a", new SaveFilmDocument(1, "fixture-save-2", film));
        Assert.Equal(2, prior.Revision);
        Assert.Equal("3ac22b79cd1033e55e32b9c306f33eedbb703bdf0ad86192467fcb5e44f760db", prior.ContentHash);
        AssertUnchanged(store, snapshot); // idempotent retry writes nothing
        var changed = studio.Documents.Read("fixture", "film-a", 2).Film!;
        changed.Title = "different payload";
        Assert.Throws<RequestConflictFault>(() => studio.Documents.Save("fixture", "film-a", new SaveFilmDocument(1, "fixture-save-2", changed)));
    }

    [Fact]
    public void TamperedV1DocumentIsRejectedButListedAsDamaged()
    {
        var store = CopyFixtureStore();
        using var studio = new Studio(store);
        var node = JsonNode.Parse(File.ReadAllText(RevisionFile(store, 2)))!;
        node["film"]!["title"] = "tampered"; // contentHash no longer matches
        File.WriteAllText(RevisionFile(store, 2), JsonSerializer.Serialize(node, DslJson.Options));
        var error = Assert.Throws<StoreIntegrityFault>(() => studio.Documents.Read("fixture", "film-a"));
        Assert.Equal("Document history is inconsistent; original files have been preserved.", Faults.Of(error).Message);
        Assert.Equal("store_integrity", Faults.Of(error).Code);
        // One damaged document must not take the whole project list down.
        studio.Documents.Save("fixture", "film-b", new SaveFilmDocument(0, "save-b", new FilmPlan { Title = "Healthy" }));
        var list = studio.Documents.List("fixture");
        Assert.Equal(2, list.Length);
        var damaged = Assert.Single(list, e => Equals(Prop(e, "id"), "film-a"));
        Assert.Equal(true, Prop(damaged, "damaged"));
        Assert.NotNull(Prop(damaged, "error"));
        Assert.Null(damaged.GetType().GetProperty("title")); // damaged entries carry no film content
        var healthy = Assert.Single(list, e => Equals(Prop(e, "Id"), "film-b"));
        Assert.Equal("Healthy", Prop(healthy, "title"));
    }

    [Fact]
    public void UnsupportedVersionNamesTheVersion()
    {
        var store = CopyFixtureStore();
        using var studio = new Studio(store);
        var node = JsonNode.Parse(File.ReadAllText(RevisionFile(store, 2)))!;
        node["version"] = 3;
        File.WriteAllText(RevisionFile(store, 2), JsonSerializer.Serialize(node, DslJson.Options));
        var ex = Assert.Throws<StoreIntegrityFault>(() => studio.Documents.Read("fixture", "film-a"));
        Assert.Equal("store_integrity", Faults.Of(ex).Code);
        Assert.Contains("version 3", ex.Message);
    }

    [Fact]
    public void V2DocumentsTolerateMissingDefaultsAndUnknownFields()
    {
        var store = TempPath("gd-v2-store-");
        using var studio = new Studio(store);
        studio.Register(new StudioProject { Id = "proj", Name = "V2", Engine = "test" });
        var film = new FilmPlan
        {
            Title = "v2 film",
            Scenes = { new FilmScene { Id = "s1",
                Performance = { new GameDirector.Core.Dsl.Cue { T = 0, Type = "actor.anim", Role = "hero", Clip = "idle", Fade = 0.2f } },
                Shots = { new FilmShot { Id = "shot", Start = 0, End = 1, Purpose = "p", Camera = new GameDirector.Core.Dsl.ShotSpec { Type = "lockoff", Subject = "hero" } } } } },
        };
        studio.Documents.Save("proj", "doc", new SaveFilmDocument(0, "save-1", film));
        var path = Path.Combine(store, "documents", "proj", "doc", "000000000001.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var cue = node["film"]!["scenes"]![0]!["performance"]![0]!.AsObject();
        cue.Remove("fade"); // a reader-side default disappears from the stored tree
        cue["futureField"] = 1; // an unknown field appears
        node["contentHash"] = ContentIdentity.OfNode(node["film"]); // integrity binds the stored tree
        File.WriteAllText(path, JsonSerializer.Serialize(node, DslJson.Options));
        var read = studio.Documents.Read("proj", "doc");
        Assert.Equal(1, read.Revision);
        Assert.Equal(0.2f, read.Film!.Scenes[0].Performance[0].Fade); // reader applies its default
    }

    [Fact]
    public void RetriedSaveIsSemanticEqualityThroughTheCurrentModel()
    {
        var store = TempPath("gd-v2-store-");
        using var studio = new Studio(store);
        studio.Register(new StudioProject { Id = "proj", Name = "V2", Engine = "test" });
        var film = new FilmPlan
        {
            Title = "v2 film",
            Scenes = { new FilmScene { Id = "s1",
                Performance = { new GameDirector.Core.Dsl.Cue { T = 0, Type = "actor.anim", Role = "hero", Clip = "idle", Fade = 0.2f } },
                Shots = { new FilmShot { Id = "shot", Start = 0, End = 1, Purpose = "p", Camera = new GameDirector.Core.Dsl.ShotSpec { Type = "lockoff", Subject = "hero" } } } } },
        };
        var saved = studio.Documents.Save("proj", "doc", new SaveFilmDocument(0, "save-1", film));
        // An older writer stored this film without the defaulted fade property.
        var path = Path.Combine(store, "documents", "proj", "doc", "000000000001.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["film"]!["scenes"]![0]!["performance"]![0]!.AsObject().Remove("fade");
        node["contentHash"] = ContentIdentity.OfNode(node["film"]); // integrity over the stored tree still passes
        File.WriteAllText(path, JsonSerializer.Serialize(node, DslJson.Options));
        var before = File.ReadAllBytes(path);
        // The identical retry (its film likewise omits fade on the wire) is accepted:
        // both sides are normalized through the current model before comparison.
        var retryFilm = DslJson.Deserialize<FilmPlan>(JsonSerializer.Serialize(node["film"], DslJson.Options));
        var prior = studio.Documents.Save("proj", "doc", new SaveFilmDocument(0, "save-1", retryFilm));
        Assert.Equal(saved.Revision, prior.Revision);
        Assert.Equal(before, File.ReadAllBytes(path)); // accepted idempotently: nothing written
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.json"));
    }

    [Fact]
    public void LegacyCrlfNewlineVariantVerifies()
    {
        var store = CopyFixtureStore();
        using var studio = new Studio(store);
        var node = JsonNode.Parse(File.ReadAllText(RevisionFile(store, 1)))!.AsObject();
        var lf = JsonSerializer.Serialize(node["film"], DslJson.Options).Replace("\r\n", "\n");
        node["contentHash"] = Sha256(lf.Replace("\n", "\r\n")); // a stamp produced on Windows
        File.WriteAllText(RevisionFile(store, 1), JsonSerializer.Serialize(node, DslJson.Options));
        var read = studio.Documents.Read("fixture", "film-a", 1);
        Assert.Equal("导演剪辑 \"v1\" <fixture> & co", read.Film!.Title);
        Assert.Equal("第二版", studio.Documents.Read("fixture", "film-a").Film!.Title); // chain intact
    }
}
