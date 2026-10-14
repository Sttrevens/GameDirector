using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

// A diagnosed failure keeps exactly the site's message, not just its status.
public class DiagnosedFailureTests : TempPaths
{
    private static void Diagnosis(Exception error, string code, int status, string? message = null, bool retryable = false)
    {
        var fault = Faults.Of(error);
        Assert.Equal(code, fault.Code); Assert.Equal(status, fault.Status); Assert.Equal(retryable, fault.Retryable);
        Assert.Equal(message ?? error.Message, fault.Message);
        Assert.Equal(fault.Message, JsonSerializer.SerializeToNode(Faults.Body(fault))!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PersistedPolicyValidationKeepsItsStorageDiagnosis()
    {
        await using var rig = await ServiceRig.Start(null);
        var path = rig.Studio.StorePath("decisions", "policies.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string content = "{\"performanceMatch\":{\"minConfidence\":2}}"; File.WriteAllText(path, content);
        var error = Assert.ThrowsAny<Exception>(() => new DecisionService(rig.Studio));
        Diagnosis(error, "store_integrity", 500, "Task performance-match confidence threshold must be within [0,1], or empty (uncalibrated).");
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task OwnedStorePathEscapeKeepsItsDiagnosis()
    {
        await using var rig = await ServiceRig.Start(null);
        Diagnosis(Assert.ThrowsAny<Exception>(() => rig.Studio.StorePath("..", "outside")), "store_integrity", 500, "Storage path escaped the Workbench store.");
    }

    [Theory]
    [InlineData("media-type", "Invalid stored media type.")]
    [InlineData("media-bytes", null)]
    [InlineData("document-history", "Document history is inconsistent; original files have been preserved.")]
    [InlineData("document-version", "Unsupported document version 99; original files have been preserved.")]
    public async Task KnownStoredIntegrityVerdictsKeepTheirDiagnosis(string site, string? message)
    {
        await using var rig = await ServiceRig.Start(null);
        Exception error;
        if (site.StartsWith("media"))
        {
            var asset = rig.WriteMedia("sound.wav", "a sound");
            if (site == "media-type")
                DslJson.SaveAtomic(rig.Studio.StorePath("media", "proj", asset.Id + ".json"), asset with { Extension = ".invalid" });
            else File.AppendAllText(rig.Studio.StorePath("media", "proj", asset.Id + ".wav"), "changed");
            message ??= "Stored audio changed: " + asset.Id;
            error = Assert.ThrowsAny<Exception>(() => MediaLibrary.Resolve(rig.Studio, "proj", asset.Id));
        }
        else
        {
            rig.Studio.Documents.Save("proj", "doc", new(0, "save", new FilmPlan { Title = "original" }));
            var path = rig.Studio.StorePath("documents", "proj", "doc", "000000000001.json");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            if (site == "document-version") node["version"] = 99; else node["film"]!["title"] = "tampered";
            File.WriteAllText(path, node.ToJsonString()); var bytes = File.ReadAllBytes(path);
            error = Assert.ThrowsAny<Exception>(() => rig.Studio.Documents.Read("proj", "doc"));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Single(rig.Studio.Documents.List("proj")); // typed damage must still be listed, not take down the project
        }
        Diagnosis(error, "store_integrity", 500, message);
    }

    [Theory]
    [InlineData("record-read")]
    [InlineData("record-list")]
    [InlineData("record-replay")]
    [InlineData("catalog-read")]
    [InlineData("catalog-status")]
    [InlineData("proposal-read")]
    [InlineData("media-read")]
    public async Task PersistedJsonParseFailuresAreStorageFailuresNotCallerInput(string site)
    {
        await using var rig = await ServiceRig.Start(null);
        string path; Func<Task> read;
        var review = new SemanticReviewRequest("read", "The hero shows relief.", null, new() { new("relief", "noul", "Does it mention relief?", null) });
        if (site.StartsWith("record"))
        {
            await rig.Decisions.SemanticReview("proj", review, CancellationToken.None);
            path = rig.Studio.StorePath("decisions", "proj", "semantic-review", "read.json");
            read = site switch
            {
                "record-read" => () => { rig.Decisions.ReadRecord("proj", "semantic-review", "read"); return Task.CompletedTask; },
                "record-list" => () => { rig.Decisions.ListRecords("proj"); return Task.CompletedTask; },
                _ => async () => { await rig.Decisions.SemanticReview("proj", review, CancellationToken.None); },
            };
        }
        else if (site.StartsWith("catalog"))
        {
            path = Path.Combine(rig.CatalogDir(), "performance-catalog.json");
            read = () => { if (site == "catalog-read") rig.Decisions.ReadCatalog("proj"); else rig.Decisions.CatalogStatus("proj"); return Task.CompletedTask; };
        }
        else if (site == "proposal-read")
        {
            path = rig.Studio.StorePath("proposals", "proj", "read.json");
            read = async () => { await new ApiDirector().Direct(new("proj", "portrait", null, false, "read"), rig.Studio, null, CancellationToken.None); };
        }
        else
        {
            path = rig.Studio.StorePath("media", "proj", new string('a', 64) + ".json");
            read = () => { MediaLibrary.List(rig.Studio, "proj"); return Task.CompletedTask; };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{");
        var expected = Assert.Throws<JsonException>(() => DslJson.Deserialize<object>("{")).Message;
        var error = await Assert.ThrowsAnyAsync<Exception>(read);
        // The parser's original text survives the typed storage boundary too.
        Assert.Contains(expected.Split(" Path: ")[0], error.Message);
        Diagnosis(error, "store_integrity", 500);
        Assert.Equal("{", File.ReadAllText(path));
    }

    [Fact]
    public async Task TakeCacheAndStoredAttemptChecksKeepTheirDiagnosis()
    {
        await using var rig = await ServiceRig.Start(null);
        var take = rig.Studio.StorePath("cache", "test-take"); Directory.CreateDirectory(take);
        File.WriteAllText(Path.Combine(take, "job.json"), "{");
        Diagnosis(Assert.Throws<TakeCacheIncompatibleException>(() => TakeJobs.Load(take)), "store_integrity", 500,
            "unreadable take job; set the entry aside and record again");
        Diagnosis(Assert.ThrowsAny<Exception>(() => TakeJobs.ResolveAttempt(take, "outside-attempts")), "store_integrity", 500, "attempt escapes job directory");
        DslJson.SaveAtomic(Path.Combine(take, "timeline.json"), new TimelineAsset { Id = "test" });
        DslJson.SaveAtomic(Path.Combine(take, "manifest.json"), rig.Manifest);
        TakeJobs.Save(take, new TakeJob { FrameRate = 6, Width = 64, Height = 64, ExpectedFrames = 6,
            TimelineSha256 = MediaTools.Hash(Path.Combine(take, "timeline.json")), ManifestSha256 = MediaTools.Hash(Path.Combine(take, "manifest.json")) });
        Diagnosis(await Assert.ThrowsAnyAsync<Exception>(() => TakeRecorder.Resume(take)), "store_integrity", 500,
            "incomplete job has no source fingerprint; preserve it and create a new take");
    }

    [Fact]
    public async Task MediaAndOutputPolicyChecksKeepTheirDiagnosis()
    {
        var missing = TempPath("missing-");
        Diagnosis(await Assert.ThrowsAnyAsync<Exception>(() => MediaTools.EnsureEncoding(MediaProfile.Default, false, missing, missing)),
            "media_failure", 500, "staged ffmpeg is missing: " + missing);
        Diagnosis(await Assert.ThrowsAnyAsync<Exception>(() => MediaTools.Run("dotnet", new[] { "--gamedirector-test-invalid-option" })), "media_failure", 500);
        Diagnosis(Assert.ThrowsAny<Exception>(() => OutputPolicy.ManifestRoot(new())), "engine_unavailable", 503,
            "Adapter must declare an existing project.sourceRoot before writing capture output.", retryable: true);
        Diagnosis(await Assert.ThrowsAnyAsync<Exception>(() => TakeRecorder.Record("unused", Path.GetTempPath(), "http://127.0.0.1:1")),
            "state_conflict", 409, "take directory already exists; choose a new take name or use resume");
        Diagnosis(await Assert.ThrowsAnyAsync<Exception>(() => EditRenderer.Render("unused", Path.GetTempPath())),
            "state_conflict", 409, "edit output exists; choose a new version directory");
    }

    [Theory]
    [InlineData("null", "media distribution manifest is empty")]
    [InlineData("{\"schemaVersion\":99}", "unsupported media distribution schema 99")]
    public void BundledMediaMetadataDiagnosisIsNotAnUnknownBug(string content, string message)
    {
        var path = TempPath("gd-media-metadata-", ".json");
        File.WriteAllText(path, content);
        try
        {
            Diagnosis(Assert.ThrowsAny<Exception>(() => MediaDistributions.Load(path)), "store_integrity", 500, message);
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaInstallUnitAndRollbackDiagnosesAreStorageFailures(bool rollback)
    {
        var root = TempPath("gd-install-diagnosis-");
        var target = Path.Combine(root, "media"); Directory.CreateDirectory(target);
        var tools = rollback ? new[] { "ffmpeg", "ffprobe" } : new[] { "ffmpeg" };
        var files = new Dictionary<string, MediaDistributions.Artifact>();
        foreach (var tool in tools)
        {
            var source = Path.Combine(root, tool + ".bin"); File.WriteAllText(source, "new " + tool);
            files[tool] = new() { Path = tool + ".bin", Sha256 = MediaTools.Hash(source) };
            File.WriteAllText(Path.Combine(target, MediaBundleInstaller.ToolFileName(tool)), "previous " + tool);
        }
        try
        {
            var descriptor = new MediaBundleDescriptor { SchemaVersion = 1, Files = files };
            var error = await Assert.ThrowsAnyAsync<Exception>(() => MediaBundleInstaller.InstallDescriptor(descriptor, root,
                new() { Tools = tools }, target, line =>
                {
                    if (rollback && line.StartsWith("installed ffmpeg"))
                    {
                        var placed = Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg"));
                        File.Delete(placed); Directory.CreateDirectory(placed); // force the rollback restore itself to fail
                        throw new IOException("fixture activation failure");
                    }
                }, CancellationToken.None, rollback ? (_, _) => Task.CompletedTask : null));
            Diagnosis(error, "store_integrity", 500);
            if (rollback)
            {
                Assert.StartsWith("Media activation failed and recovery needs attention; previous files are preserved in ", error.Message);
                var backup = Directory.GetDirectories(target, ".backup-*").Single();
                Assert.Equal("previous ffmpeg", File.ReadAllText(Path.Combine(backup, MediaBundleInstaller.ToolFileName("ffmpeg"))));
            }
            else Assert.Equal("The built-in capability check requires the ffmpeg+ffprobe install unit.", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StoredTakeReceiptMismatchKeepsItsStorageDiagnosis()
    {
        await using var rig = await ServiceRig.Start(null);
        var take = rig.Studio.StorePath("cache", "receipt-test"); Directory.CreateDirectory(Path.Combine(take, "frames"));
        DslJson.SaveAtomic(Path.Combine(take, "timeline.json"), new TimelineAsset { Id = "test" });
        DslJson.SaveAtomic(Path.Combine(take, "manifest.json"), rig.Manifest);
        TakeJobs.Save(take, new TakeJob { TakeId = "owned", State = TakeJobStates.FramesComplete, FrameRate = 6, Width = 64, Height = 64, ExpectedFrames = 1,
            TimelineSha256 = MediaTools.Hash(Path.Combine(take, "timeline.json")), ManifestSha256 = MediaTools.Hash(Path.Combine(take, "manifest.json")) });
        File.WriteAllText(Path.Combine(take, "frames", "000000.png"), "retained-frame"); TakeJobs.Checkpoint(take, 0);
        File.WriteAllText(Path.Combine(take, "receipt.json"), "{\"take\":{\"takeId\":\"other\",\"state\":\"Completed\",\"capturedFrames\":1}}");
        Diagnosis(await Assert.ThrowsAnyAsync<Exception>(() => TakeRecorder.Resume(take)), "store_integrity", 500, "incomplete or mismatched take receipt");
        Assert.True(File.Exists(Path.Combine(take, "frames", "000000.png")));
    }

    [Fact]
    public async Task BridgeRejectionKeepsItsEngineDiagnosis()
    {
        await using var rig = await ServiceRig.Start(null);
        using var bridge = new DirectorBridgeClient(rig.Studio.Project("proj").Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => bridge.BeginTakeAsync(new GameDirector.Core.Capture.TakeRequest()));
        Diagnosis(error, "not_found", 404); // an answering fixture with no take route is not unreachable
        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task DiagnosedProviderFailureKeepsEndpointRecoveryAdvice()
    {
        var handler = new TestRig.RecordingHandler(TestRig.Json("{}", System.Net.HttpStatusCode.Unauthorized));
        var generator = new ChatCompletionsPlanGenerator(new("http://127.0.0.1:9/chat", "fixture", ""), handler);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => generator.Generate(new("portrait", null),
            new(TestRig.Manifest(Path.GetTempPath()), Array.Empty<MediaAsset>(), null, _ => null), CancellationToken.None));
        Diagnosis(error, "provider_failure", 500, "Model API returned HTTP 401; check the endpoint, key and model. No automatic network retry was made.");
    }
}
