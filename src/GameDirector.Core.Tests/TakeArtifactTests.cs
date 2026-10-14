using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Core.Tests;
using Xunit;

[Collection("DistributionEnv")]
public class TakeArtifactTests : TempPaths
{
    [Fact] public void Receipts_written_before_the_typed_record_still_read_and_round_trip()
    {
        // Fixture: the exact take.json payload the pre-record anonymous writer
        // emitted (DslJson camelCase). Field drift breaks this test, not a cache.
        const string json = """
        {
          "version": 1,
          "takeId": "9f8e7d6c5b4a39281706f5e4d3c2b1a0",
          "timelineId": "scene-01/shot-01",
          "state": "Verified",
          "frameCount": 24,
          "frameRate": 24,
          "width": 1280,
          "height": 720,
          "duration": 1,
          "video": "picture.mp4",
          "videoSha256": "0000000000000000000000000000000000000000000000000000000000000001",
          "timelineSha256": "0000000000000000000000000000000000000000000000000000000000000002",
          "manifestSha256": "0000000000000000000000000000000000000000000000000000000000000003",
          "audio": "silent-picture; mix audio in the edit"
        }
        """;
        var artifact = DslJson.Deserialize<TakeArtifact>(json);
        Assert.Equal(1, artifact.Version);
        Assert.Equal("9f8e7d6c5b4a39281706f5e4d3c2b1a0", artifact.TakeId);
        Assert.Equal("scene-01/shot-01", artifact.TimelineId);
        Assert.Equal(TakeJobStates.Verified, artifact.State);
        Assert.Equal(24, artifact.FrameCount); Assert.Equal(0, artifact.CaptureFromFrame);
        Assert.Equal(24, artifact.FrameRate);
        Assert.Equal(1280, artifact.Width);
        Assert.Equal(720, artifact.Height);
        Assert.Equal(1, artifact.Duration);
        Assert.Equal("picture.mp4", artifact.Video);
        Assert.EndsWith("01", artifact.VideoSha256);
        Assert.EndsWith("02", artifact.TimelineSha256);
        Assert.EndsWith("03", artifact.ManifestSha256);
        Assert.Equal(GameDirector.Core.Capture.TakeReceipt.SilentPictureAudio, artifact.Audio);
        Assert.Equal(artifact, DslJson.Deserialize<TakeArtifact>(DslJson.Serialize(artifact)));
        // Additive evolution: old receipts still mean full capture; new writers echo zero.
        var keys = JsonNode.Parse(DslJson.Serialize(artifact))!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "audio", "captureFromFrame", "duration", "frameCount", "frameRate", "height", "manifestSha256", "state", "takeId", "timelineId", "timelineSha256", "version", "video", "videoSha256", "width" }, keys);
    }

    [Theory] [InlineData(0)] [InlineData(3)]
    public async Task Verified_fast_path_reuses_the_picture_without_reading_frames(int captureFromFrame)
    {
        var root = NewRoot();
        try
        {
            var take = VerifiedTake(root, captureFromFrame);
            // The frame directory is gone and the journal is corrupt: reuse here
            // proves the fast path never paid for a frame read or hash.
            File.WriteAllText(Path.Combine(take, "frames.jsonl"), "{\"frameIndex\":0,\"sha256\":\"bogus\"}\n");
            var result = await TakeRecorder.Resume(take);
            Assert.True(result.Reused);
            Assert.Equal(OutputPolicy.Child(take, "picture.mp4"), result.Video);
            Assert.False(Directory.Exists(Path.Combine(take, "attempts")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public async Task Verified_take_with_lost_picture_and_no_retained_frames_replays_with_engine()
    {
        var root = NewRoot();
        try
        {
            var take = VerifiedTake(root);
            File.WriteAllText(Path.Combine(take, "picture.mp4"), "corrupt");
            // The explicit trade-off of reclaiming frames: picture loss costs a
            // replay, and a replay needs the engine (here: unreachable port).
            var error = await Assert.ThrowsAsync<BridgeException>(() => TakeRecorder.Resume(take));
            Assert.Equal(BridgeException.FailureKind.Unreachable, error.Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("owned", "owned")]
    [InlineData("owned", "foreign")]
    [InlineData(null, "unknown")]
    public async Task Resume_inspects_take_ownership_before_a_capture_blocked_manifest(string? ownedId, string liveId)
    {
        var root = NewRoot(); var routes = new List<string>(); var capturing = true;
        await using var bridge = new GameDirector.Core.Tests.BridgeHttpFixture(async ctx => {
            var route = ctx.Request.Url!.AbsolutePath; routes.Add(route);
            if (route == "/take") await GameDirector.Core.Tests.BridgeHttpFixture.Reply(ctx, 200,
                DslJson.Serialize(new { take = new { takeId = liveId, state = "Capturing" } }));
            else if (route == "/take/stop") {
                using var reader = new StreamReader(ctx.Request.InputStream);
                Assert.Equal("owned", JsonNode.Parse(await reader.ReadToEndAsync())!["takeId"]!.GetValue<string>());
                capturing = false; await GameDirector.Core.Tests.BridgeHttpFixture.Reply(ctx, 200, "{\"ok\":true}");
            }
            else await GameDirector.Core.Tests.BridgeHttpFixture.Reply(ctx, capturing ? 409 : 200,
                capturing ? "{\"error\":\"busy capturing\"}" : DslJson.Serialize(new CapabilityManifest { Game = "changed-source", Capabilities = new() { [CapabilityKeys.ProjectSourceRoot] = Path.Combine(root, "source") } }));
        });
        try {
            var take = JournalTake(root, new TakeJob { State = TakeJobStates.Interrupted, Endpoint = bridge.Endpoint, TakeId = ownedId,
                FrameRate = 6, Width = 64, Height = 64, ExpectedFrames = 2, SourceFingerprint = "fixture" });
            var error = await Assert.ThrowsAsync<StateConflictException>(() => TakeRecorder.Resume(take));
            if (ownedId == liveId) {
                Assert.Contains("live source changed", error.Message); Assert.Equal(new[] { "/take", "/take/stop", "/manifest" }, routes);
            }
            else { Assert.Contains("unacknowledged", error.Message); Assert.Equal(new[] { "/take" }, routes); }
            Assert.False(Directory.Exists(Path.Combine(take, "attempts")));
        }
        finally { Directory.Delete(root, true); }
    }

    [MediaTheory] [InlineData(0)] [InlineData(3)]
    public async Task Default_retention_reclaims_frames_keep_frames_preserves_them(int captureFromFrame)
    {
        foreach (var retainFrames in new[] { false, true })
        {
            var root = NewRoot();
            try
            {
                var take = FramesCompleteTake(root, retainFrames, captureFromFrame);
                var result = await TakeRecorder.Resume(take);
                Assert.False(result.Reused); // re-encoded from the verified frame journal
                Assert.Equal(File.Exists(Path.Combine(take, "frames", "000000.png")), retainFrames);
                Assert.True(File.Exists(Path.Combine(take, "frames.jsonl"))); // small provenance always stays
                Assert.True(File.Exists(result.Video));
                var artifact = DslJson.Load<TakeArtifact>(Path.Combine(take, "take.json"));
                Assert.Equal(TakeJobStates.Verified, artifact.State); Assert.Equal(captureFromFrame, artifact.CaptureFromFrame); Assert.Equal(2, artifact.FrameCount);
                Assert.Equal(TakeJobStates.Verified, TakeJobs.Load(take).State);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    // A 64x64 RGB PNG, matching the geometry the fixtures declare.
    private const string Png64 = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAeklEQVR4nO3PUQkAIBTAwBfFXKYzqiH8OITBAtxm7fN1wwUNaEEDWtCAFjSgBQ1oQQNa0IAWNKAFDWhBA1rQgBY0oAUNaEEDWtCAFjSgBQ1oQQNa0IAWNKAFDWhBA1rQgBY0oAUNaEEDWtCAFjSgBQ1oQQNa0IAWPHYBG0uhAFP1IZYAAAAASUVORK5CYII=";

    private string NewRoot()
    {
        var root = TempPath("gd-take-");
        Directory.CreateDirectory(Path.Combine(root, "source"));
        return root;
    }
    private static string JournalTake(string root, TakeJob job)
    {
        var take = Path.Combine(root, "take-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(take);
        var manifest = new CapabilityManifest { Game = "fixture", Capabilities = new() {
            [CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox,
            [CapabilityKeys.PresentationSourceFingerprint] = "fixture",
            [CapabilityKeys.ProjectSourceRoot] = Path.Combine(root, "source") } };
        DslJson.Save(Path.Combine(take, "manifest.json"), manifest);
        DslJson.Save(Path.Combine(take, "timeline.json"), new TimelineAsset { Id = "fixture" });
        job.TimelineSha256 = MediaTools.Hash(Path.Combine(take, "timeline.json"));
        job.ManifestSha256 = MediaTools.Hash(Path.Combine(take, "manifest.json"));
        TakeJobs.Save(take, job);
        return take;
    }
    private static string VerifiedTake(string root, int captureFromFrame = 0)
    {
        var take = JournalTake(root, new TakeJob { State = TakeJobStates.Verified, Endpoint = "http://127.0.0.1:1", TakeId = "take-1",
            FrameRate = 6, Width = 64, Height = 64, ExpectedFrames = 2, CaptureFromFrame = captureFromFrame, SourceFingerprint = "fixture" });
        File.WriteAllBytes(Path.Combine(take, "picture.mp4"), new byte[] { 1, 2, 3, 4 });
        DslJson.SaveAtomic(Path.Combine(take, "take.json"), new TakeArtifact { TakeId = "take-1", TimelineId = "fixture", State = TakeJobStates.Verified,
            FrameCount = 2, CaptureFromFrame = captureFromFrame, FrameRate = 6, Width = 64, Height = 64, Duration = 2.0 / 6,
            VideoSha256 = MediaTools.Hash(Path.Combine(take, "picture.mp4")),
            TimelineSha256 = TakeJobs.Load(take).TimelineSha256, ManifestSha256 = TakeJobs.Load(take).ManifestSha256 });
        return take;
    }
    private static string FramesCompleteTake(string root, bool retainFrames, int captureFromFrame)
    {
        var take = JournalTake(root, new TakeJob { State = TakeJobStates.FramesComplete, Endpoint = "http://127.0.0.1:1", TakeId = "take-1",
            FrameRate = 6, Width = 64, Height = 64, ExpectedFrames = 2, CaptureFromFrame = captureFromFrame, SourceFingerprint = "fixture", RetainFrames = retainFrames });
        var frames = Path.Combine(take, "frames"); Directory.CreateDirectory(frames);
        for (var i = 0; i < 2; i++)
        {
            File.WriteAllBytes(Path.Combine(frames, $"{i:D06}.png"), Convert.FromBase64String(Png64));
            TakeJobs.Checkpoint(take, i);
        }
        DslJson.Save(Path.Combine(take, "receipt.json"), new TakeSnapshot { Take = new GameDirector.Core.Capture.TakeReceipt {
            TakeId = "take-1", TimelineId = "fixture", State = "Completed", FrameRate = 6, Width = 64, Height = 64,
            FrameCount = 2 + captureFromFrame, CaptureFromFrame = captureFromFrame, CapturedFrames = 2 } });
        return take;
    }
}
