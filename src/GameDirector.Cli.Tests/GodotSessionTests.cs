using System.Text.Json;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using GameDirector.Godot;
using Xunit;

namespace GameDirector.Cli.Tests;

public sealed class GodotSessionTests
{
    private static TakeRequest Request(int from = 0) => new() { FrameRate = 2, Width = 64, Height = 64, CaptureFromFrame = from,
        Timeline = new() { Id = "fixture", Cues = new() { new() { Type = CueTypes.CameraShot, Shot = new() {
            Subject = "hero", From = "anchor", DurationSeconds = 2 } } } } };
    private static FrameRequest Frame(TakeReceipt t, int i, bool advance = false) => new() { TakeId = t.TakeId, FrameIndex = i, AdvanceOnly = advance };
    private static async Task Error(BridgeErrorDefinition definition, Func<Task> action) =>
        Assert.Same(definition, (await Assert.ThrowsAsync<BridgeRequestException>(action)).Error);

    // These catch advancing a retry, accepting wrong identities/order, or losing final cached bytes on cleanup.
    [Fact]
    public async Task Sequential_frames_cache_only_the_last_index_even_after_completion()
    {
        var factory = new Factory(); await using var s = new GodotSession(factory);
        var t = await s.Start(Request(), default);
        await Error(BridgeErrors.StateConflict, () => s.Frame(Frame(t, 1), default));
        await Error(BridgeErrors.StateConflict, () => s.Frame(new() { TakeId = "foreign" }, default));
        Assert.Equal(new byte[] { 0 }, await s.Frame(Frame(t, 0), default));
        Assert.Equal(new byte[] { 0 }, await s.Frame(Frame(t, 0), default));
        for (var i = 1; i < 4; i++) Assert.Equal(new byte[] { (byte)i }, await s.Frame(Frame(t, i), default));
        Assert.Equal(TakeStates.Completed, t.State); Assert.Equal(4, t.CapturedFrames);
        Assert.Equal(new byte[] { 3 }, await s.Frame(Frame(t, 3), default));
        await Error(BridgeErrors.StateConflict, () => s.Frame(Frame(t, 2), default));
        Assert.True(factory.Worker.Disposed);
    }

    [Fact]
    public async Task Preroll_requires_advance_only_and_retries_do_not_capture()
    {
        var factory = new Factory(); await using var s = new GodotSession(factory);
        var t = await s.Start(Request(2), default);
        await Error(BridgeErrors.StateConflict, () => s.Frame(Frame(t, 0), default));
        Assert.Null(await s.Frame(Frame(t, 0, true), default)); Assert.Null(await s.Frame(Frame(t, 0, true), default));
        Assert.Null(await s.Frame(Frame(t, 1, true), default));
        await Error(BridgeErrors.StateConflict, () => s.Frame(Frame(t, 2, true), default));
        await s.Frame(Frame(t, 2), default); await s.Frame(Frame(t, 3), default);
        Assert.Equal(2, t.CapturedFrames); Assert.Equal(TakeStates.Completed, t.State);
    }

    [Fact]
    public async Task Admission_errors_are_400_stage_preflight_and_active_start_are_409()
    {
        var factory = new Factory(); await using var s = new GodotSession(factory);
        var bad = Request(); bad.Width = 65;
        await Error(BridgeErrors.InvalidRequest, () => s.Start(bad, default));
        factory.Refuse = true; await Error(BridgeErrors.StateConflict, () => s.Start(Request(), default));
        factory.Refuse = false;
        var r = Request(); r.SourceFingerprint = "stale";
        await Error(BridgeErrors.StateConflict, () => s.Start(r, default));
        var t = await s.Start(Request(), default);
        await Error(BridgeErrors.StateConflict, () => s.Start(Request(), default));
        await Error(BridgeErrors.StateConflict, () => s.Stop(new() { TakeId = "foreign" }));
        await s.Stop(new() { TakeId = t.TakeId }); await s.Stop(new() { TakeId = t.TakeId });
        Assert.Equal(TakeStates.Cancelled, t.State); Assert.True(factory.Worker.Disposed);
    }

    [Fact]
    public async Task Only_matching_take_requests_renew_the_idle_lease()
    {
        var clock = new Clock(); var factory = new Factory(); await using var s = new GodotSession(factory, clock);
        var t = await s.Start(Request(), default);
        clock.Now += TimeSpan.FromSeconds(CaptureContract.IdleLeaseSeconds);
        await Error(BridgeErrors.StateConflict, () => s.Frame(new() { TakeId = "foreign" }, default));
        clock.Now += TimeSpan.FromSeconds(1); await s.ExpireLease();
        Assert.Equal(TakeStates.Cancelled, t.State); Assert.True(factory.Worker.Disposed);
    }

    [Fact]
    public async Task Measure_rows_are_collected_once_and_never_allowed_on_replay()
    {
        var factory = new Factory(); factory.Manifest.Capabilities[CapabilityKeys.Measure] = CapabilityKeys.MeasureV1;
        await using var s = new GodotSession(factory); var t = await s.Start(Request(1), default);
        await Error(BridgeErrors.InvalidRequest, () => s.Frame(new() { TakeId = t.TakeId, FrameIndex = 0, AdvanceOnly = true, Measure = true }, default));
        await s.Frame(Frame(t, 0, true), default);
        var request = new FrameRequest { TakeId = t.TakeId, FrameIndex = 1, Measure = true };
        await s.Frame(request, default); await s.Frame(request, default);
        var snapshot = JsonSerializer.SerializeToElement(s.Snapshot());
        Assert.Single(snapshot.GetProperty("measures").EnumerateArray());
        Assert.Equal(1, snapshot.GetProperty("measures")[0].GetProperty("frame").GetInt32());
        await s.Stop(new() { TakeId = t.TakeId });
        await s.Start(Request(), default); Assert.Empty(JsonSerializer.SerializeToElement(s.Snapshot()).GetProperty("measures").EnumerateArray());
    }

    internal sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UnixEpoch; public override DateTimeOffset GetUtcNow() => Now; }
    internal sealed class Factory : IGodotWorkerFactory
    {
        public bool Refuse; public Worker Worker = new();
        public CapabilityManifest Manifest { get; } = new() { Game = "Fixture", Roles = new() { new() { Id = "hero", DefaultActor = "body", PresentAtStart = true } },
            Actors = new() { new() { Id = "body" } }, Locations = new() { new() { Id = "anchor", Position = new float[] { 0, 1, 4 } } },
            ShotTypes = ShotVocabulary.ShotIds, FrameTypes = ShotVocabulary.FrameIds,
            Capabilities = new() { [CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox, [CapabilityKeys.PresentationSourceFingerprint] = "source" } };
        public void AssertFresh() { }
        public Task<IGodotWorker> Open(CancellationToken ct) { Worker = new() { Refuse = Refuse }; return Task.FromResult<IGodotWorker>(Worker); }
    }
    internal sealed class Worker : IGodotWorker
    {
        public bool Disposed, Refuse; private int next;
        public Task Begin(CompiledTimeline timeline, int width, int height, CancellationToken ct) {
            if (Refuse) throw new BridgeRequestException(BridgeErrors.StateConflict, "stage/Particles: unowned clock"); return Task.CompletedTask; }
        public Task<WorkerFrame> Frame(int index, int fps, bool advanceOnly, CancellationToken ct, bool measure = false) {
            Assert.Equal(next++, index); return Task.FromResult(new WorkerFrame(advanceOnly ? null : new byte[] { (byte)index },
                Measure: measure ? JsonSerializer.SerializeToElement(new { frame = index, time = FrameClock.TimeOf(index, fps) }) : null)); }
        public Task<JsonElement> Finish(CancellationToken ct) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
