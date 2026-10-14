using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

/// <summary>The store-level schema guard (store schema guard): the one entrance that keeps an
/// older binary from silently dropping a newer store's fields, and stamps pre-guard
/// stores so the seam exists from now on.</summary>
public class StoreSchemaGuardTests : TempPaths
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-store");

    private string TempStore() => TempPath("gd-schema-");
    private string CopyFixtureStore()
    {
        var store = TempStore();
        CopyDirectory(FixtureRoot, store);
        return store;
    }
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(source, target));
    }
    private static Dictionary<string, byte[]> Snapshot(string store) =>
        Directory.GetFiles(store, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(store, f), Fingerprint);
    // A live Studio holds .owner with FileShare.None (Windows refuses to read it), so the
    // ownership lock is compared by size and write time; every data file by its bytes.
    private static byte[] Fingerprint(string file)
    {
        if (Path.GetFileName(file) != ".owner") return File.ReadAllBytes(file);
        var info = new FileInfo(file);
        return BitConverter.GetBytes(info.Length).Concat(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks)).ToArray();
    }
    private static void AssertByteIdentical(string store, Dictionary<string, byte[]> snapshot)
    {
        Assert.Equal(snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal),
            Directory.GetFiles(store, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(store, f)).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (file, bytes) in snapshot) Assert.Equal(bytes, Fingerprint(Path.Combine(store, file)));
    }
    private static JsonObject WriteStamp(string store, int schemaVersion, int minReaderSchema, string writerProductVersion)
    {
        var node = new JsonObject
        {
            ["schemaVersion"] = schemaVersion,
            ["minReaderSchema"] = minReaderSchema,
            ["writerProductVersion"] = writerProductVersion,
        };
        File.WriteAllText(Path.Combine(store, StoreSchema.FileName), node.ToJsonString());
        return node;
    }
    private static StoreSchema.State ReadStamp(string store) => DslJson.Load<StoreSchema.State>(Path.Combine(store, StoreSchema.FileName));

    [Fact]
    public void NewEmptyStoreIsStampedSchema2First()
    {
        var store = TempStore(); // not even created yet
        using var studio = new Studio(store);
        var stamp = ReadStamp(store);
        Assert.Equal(StoreSchema.CurrentSchema, stamp.SchemaVersion);
        Assert.Equal(StoreSchema.MinReaderSchema, stamp.MinReaderSchema);
        Assert.False(string.IsNullOrWhiteSpace(stamp.WriterProductVersion));
        Assert.Equal(2, StoreSchema.CurrentSchema); // 2 = FilmDocument v2 + catalog v2
    }

    [Fact]
    public void PreGuardStoreOpensReadsAndGainsTheStamp()
    {
        var store = CopyFixtureStore();
        var snapshot = Snapshot(store);
        using var studio = new Studio(store);
        var latest = studio.Documents.Read("fixture", "film-a");
        Assert.Equal(2, latest.Revision);
        Assert.Equal("第二版", latest.Film!.Title);
        var stamp = ReadStamp(store);
        Assert.Equal(StoreSchema.CurrentSchema, stamp.SchemaVersion);
        foreach (var (file, bytes) in snapshot) Assert.Equal(bytes, Fingerprint(Path.Combine(store, file)));
    }

    [Fact]
    public void TooNewStoreThrowsAndLeavesTheDirectoryByteIdentical()
    {
        var store = CopyFixtureStore();
        WriteStamp(store, schemaVersion: 99, minReaderSchema: 99, writerProductVersion: "9.9.9-test");
        var snapshot = Snapshot(store);
        var ex = Assert.Throws<StoreIntegrityFault>(() => new Studio(store));
        Assert.Equal("store_integrity", Faults.Of(ex).Code);
        Assert.Equal(ex.Message, Faults.Of(ex).Message);
        Assert.Contains("9.9.9-test", ex.Message);
        Assert.Contains("update GameDirector", ex.Message);
        AssertByteIdentical(store, snapshot); // no .owner, no new files, no rewrites
        Assert.False(File.Exists(Path.Combine(store, ".owner")));
    }

    [Theory]
    [InlineData("{ not json")]                                                            // unparseable
    [InlineData("{}")]                                                                    // no fields: defaults are not "current"
    [InlineData("{\"schemaVersion\":2,\"writerProductVersion\":\"0.1\"}")]               // missing minReaderSchema
    [InlineData("{\"schemaVersion\":2,\"minReaderSchema\":2}")]                          // missing writerProductVersion
    [InlineData("{\"schemaVersion\":2,\"minReaderSchema\":9,\"writerProductVersion\":\"0.1\"}")] // minReader above schema
    [InlineData("{\"schemaVersion\":-1,\"minReaderSchema\":1,\"writerProductVersion\":\"0.1\"}")] // non-positive schema
    public void CorruptStampThrowsAndIsNeverOverwritten(string content)
    {
        var store = TempStore();
        Directory.CreateDirectory(store);
        var path = Path.Combine(store, StoreSchema.FileName);
        File.WriteAllText(path, content);
        var ex = Assert.Throws<StoreIntegrityFault>(() => new Studio(store));
        Assert.Equal("store_integrity", Faults.Of(ex).Code);
        Assert.Equal(ex.Message, Faults.Of(ex).Message);
        Assert.Contains(path, ex.Message);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidSavedProjectIsNotStamped()
    {
        // Refusal checks are read-only: a store failing project validation must not
        // even gain the schema stamp.
        var store = TempStore();
        Directory.CreateDirectory(Path.Combine(store, "projects"));
        File.WriteAllText(Path.Combine(store, "projects", "broken.json"), "{\"id\":\"../escape\",\"name\":\"x\",\"engine\":\"test\"}");
        var snapshot = Snapshot(store);
        var error = Assert.Throws<StoreIntegrityFault>(() => new Studio(store));
        Assert.Equal("Invalid saved project ID.", error.Message);
        Assert.Equal("store_integrity", Faults.Of(error).Code);
        Assert.False(File.Exists(Path.Combine(store, StoreSchema.FileName)));
        AssertByteIdentical(store, snapshot);
    }

    [Fact]
    public void OwnedStoreIsNotWrittenByASecondConstructor()
    {
        // A second Workbench must fail on ownership without having written anything:
        // the stamp lands only after .owner is held.
        var store = CopyFixtureStore();
        // FileShare.Read still blocks Studio's ReadWrite+FileShare.None open (exactly
        // like the running instance's lock) while letting Snapshot read the bytes back.
        var ownership = new FileStream(Path.Combine(store, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var snapshot = Snapshot(store);
            Assert.Throws<IOException>(() => new Studio(store));
            Assert.False(File.Exists(Path.Combine(store, StoreSchema.FileName)));
            AssertByteIdentical(store, snapshot);
        }
        finally { ownership.Dispose(); }
    }

    [Fact]
    public void SchemaVersionNeverDecreases()
    {
        // A newer writer that still allows this reader: the stamp is left untouched.
        var newer = TempStore();
        Directory.CreateDirectory(newer);
        WriteStamp(newer, schemaVersion: 5, minReaderSchema: 1, writerProductVersion: "5.0.0-test");
        using (var studio = new Studio(newer))
            Assert.Equal(5, ReadStamp(newer).SchemaVersion);
        // An older writer's stamp advances to current only after a successful load.
        var older = TempStore();
        Directory.CreateDirectory(older);
        WriteStamp(older, schemaVersion: 1, minReaderSchema: 1, writerProductVersion: "0.1.0-test");
        using (var studio = new Studio(older))
        {
            var stamp = ReadStamp(older);
            Assert.Equal(StoreSchema.CurrentSchema, stamp.SchemaVersion);
            Assert.Equal(StoreSchema.MinReaderSchema, stamp.MinReaderSchema);
        }
    }
}
