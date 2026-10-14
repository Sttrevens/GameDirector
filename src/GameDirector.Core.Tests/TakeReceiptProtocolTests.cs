using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
[Collection("DistributionEnv")]
public class TakeReceiptProtocolTests : TempPaths
{
    private static (TimelineAsset Timeline, AdmissionResult Admission) Admitted()
    {
        var asset = new TimelineAsset { Id = "receipt-probe", Cues = new() { TestAssets.Shot(0, duration: 1) } };
        var manifest = TestAssets.Manifest();
        manifest.Capabilities[CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox;
        return (asset, TakeAdmission.Admit(asset, manifest, 1, 64, 64, 0));
    }
    private static TakeReceipt StartReceipt(TimelineAsset timeline, AdmissionResult admission) =>
        new() { TakeId = "owned", TimelineId = timeline.Id, State = TakeStates.Capturing,
            FrameCount = admission.FrameCount, FrameRate = 1, Width = 64, Height = 64 };

    [Fact]
    public void AStartReceiptFieldDiffNamesTheFieldAndIsNonReplayable()
    {
        var (timeline, admission) = Admitted();
        var receipt = StartReceipt(timeline, admission); receipt.FrameCount = 61;
        var ex = Assert.Throws<EngineProtocolException>(() => TakeRecorder.ValidateStartReceipt(receipt, admission, 0, 1, 64, 64, timeline.Id));
        Assert.Equal("start receipt frameCount 61 != admission " + admission.FrameCount, ex.Message);
    }

    [Fact]
    public void EveryStartReceiptFieldIsCheckedAndAgreementPasses()
    {
        var (timeline, admission) = Admitted();
        TakeRecorder.ValidateStartReceipt(StartReceipt(timeline, admission), admission, 0, 1, 64, 64, timeline.Id);
        foreach (var (field, mutate) in new (string, Action<TakeReceipt>)[]
        {
            ("state", r => r.State = TakeStates.Completed),
            ("takeId is empty", r => r.TakeId = " "),
            ("frameCount", r => r.FrameCount = 99),
            ("captureFromFrame", r => r.CaptureFromFrame = 5),
            ("frameRate", r => r.FrameRate = 2),
            ("width", r => r.Width = 65),
            ("height", r => r.Height = 65),
            ("timelineId", r => r.TimelineId = "other"),
        })
        {
            var receipt = StartReceipt(timeline, admission); mutate(receipt);
            var ex = Assert.Throws<EngineProtocolException>(() => TakeRecorder.ValidateStartReceipt(receipt, admission, 0, 1, 64, 64, timeline.Id));
            Assert.Contains(field, ex.Message);
        }
    }

    [Fact]
    public void FinalReceiptDiffsNameTheFieldWhileStoredVerdictsStayStoreIntegrity()
    {
        var job = new TakeJob { TakeId = "owned", FrameRate = 1, Width = 64, Height = 64, ExpectedFrames = 1, CaptureFromFrame = 0 };
        TakeReceipt Good() => new() { TakeId = "owned", State = TakeStates.Completed, CapturedFrames = 1, FrameCount = 1, CaptureFromFrame = 0 };
        TakeRecorder.ValidateReceipt(new TakeSnapshot { Take = Good() }, job);
        foreach (var (field, mutate) in new (string, Action<TakeReceipt>)[] {
            ("takeId", r => r.TakeId = "other"), ("state", r => r.State = TakeStates.Failed),
            ("capturedFrames", r => r.CapturedFrames = 0), ("frameCount", r => r.FrameCount = 2),
            ("captureFromFrame", r => r.CaptureFromFrame = 1)
        }) {
            var broken = new TakeSnapshot { Take = Good() }; mutate(broken.Take!);
            var live = Assert.Throws<EngineProtocolException>(() => TakeRecorder.ValidateReceipt(broken, job));
            Assert.Contains("final receipt " + field, live.Message);
            Assert.Throws<StoreIntegrityException>(() => TakeRecorder.ValidateReceipt(broken, job, stored: true));
        }
        Assert.Throws<EngineProtocolException>(() => TakeRecorder.ValidateReceipt(new TakeSnapshot(), job));
    }

    [MediaTheory] [InlineData("version")] [InlineData("duplicate")] [InlineData("capability")]
    public async Task RecordRefusesInvalidEngineManifestBeforeStartingATake(string kind)
    {
        var root = TempPath("gd-manifest-protocol-");
        Directory.CreateDirectory(root);
        var timeline = Path.Combine(root, "timeline.json");
        DslJson.Save(timeline, Admitted().Timeline);
        var started = 0;
        await using var server = new BridgeHttpFixture(ctx => {
            if (ctx.Request.Url!.AbsolutePath == "/manifest")
                return BridgeHttpFixture.Reply(ctx, 200, DslJson.Serialize(TestAssets.InvalidManifest(kind)));
            Interlocked.Increment(ref started);
            return BridgeHttpFixture.Reply(ctx, 200, "{}");
        });
        try {
            var ex = await Assert.ThrowsAsync<EngineProtocolException>(() => TakeRecorder.Record(timeline, Path.Combine(root, "take"), server.Endpoint, 1, 64, 64));
            Assert.Contains("manifest", ex.Message);
            Assert.Equal(0, started);
            Assert.False(Directory.Exists(Path.Combine(root, "take")));
        } finally { Directory.Delete(root, true); }
    }

    // The pure protocol tests above always run. Encoder-dependent wiring uses
    // a discovery-time skip (xUnit v2), never a silently passing early return.
    [MediaTheory] [InlineData(true)] [InlineData(false)]
    public async Task DisagreeingEngineReceiptsAreNonReplayableAndNameTheField(bool start)
    {
        var root = TempPath("gd-receipt-protocol-");
        Directory.CreateDirectory(Path.Combine(root, "source"));
        var asset = new TimelineAsset { Id = "receipt-probe", Cues = new() { TestAssets.Shot(0, duration: 1) } };
        var timeline = Path.Combine(root, "timeline.json"); DslJson.Save(timeline, asset);
        var manifest = TestAssets.Manifest(); manifest.Capabilities[CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox;
        manifest.Capabilities[CapabilityKeys.ProjectSourceRoot] = Path.Combine(root, "source");
        var receipt = new TakeReceipt { TakeId = "owned", TimelineId = asset.Id, State = TakeStates.Capturing,
            FrameCount = start ? 2 : 1, FrameRate = 1, Width = 64, Height = 64 };
        await using var server = new BridgeHttpFixture(ctx => {
            var route = ctx.Request.Url!.AbsolutePath;
            if (route == "/manifest") return BridgeHttpFixture.Reply(ctx, 200, DslJson.Serialize(manifest));
            if (route == "/take/start") return BridgeHttpFixture.Reply(ctx, 200, DslJson.Serialize(receipt));
            if (route == "/take/frame") return BridgeHttpFixture.Reply(ctx, 200, BridgeHttpFixture.Png, "image/png");
            if (route == "/take") { receipt.State = TakeStates.Completed; return BridgeHttpFixture.Reply(ctx, 200, DslJson.Serialize(new { take = receipt })); }
            return BridgeHttpFixture.Reply(ctx, 200, "{}");
        });
        try {
            var ex = await Assert.ThrowsAsync<EngineProtocolException>(() => TakeRecorder.Record(timeline, Path.Combine(root, "take"), server.Endpoint, 1, 64, 64));
            Assert.Contains(start ? "start receipt frameCount 2 != admission 1" : "final receipt capturedFrames 0 != job 1", ex.Message);
        } finally { Directory.Delete(root, true); }
    }
}
