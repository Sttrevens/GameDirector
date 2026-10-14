using System.Net;
using System.Text;
using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using GameDirector.Godot;
using Xunit;

namespace GameDirector.Cli.Tests;

public sealed class GodotQueueTests
{
    // Catches bypassing the serial queue, unbounded pending work, or blocking health behind a renderer.
    [Fact]
    public async Task Queue_is_bounded_and_healthy_while_a_frame_is_blocked()
    {
        var factory = new Factory(); await using var bridge = await GodotBridge.Start(factory, 0);
        using var client = new HttpClient { BaseAddress = new(bridge.Endpoint), Timeout = TestBudget.Hang };
        var timeline = new TimelineAsset { Id = "queue", Cues = new() { new() { Type = CueTypes.CameraShot,
            Shot = new() { Subject = "hero", From = "anchor", DurationSeconds = 2 } } } };
        using var start = await client.PostAsync(BridgeRoutes.TakeStart, Json(new TakeRequest { Timeline = timeline, Width = 64, Height = 64, FrameRate = 2 }));
        var t = DslJson.Deserialize<TakeReceipt>(await start.Content.ReadAsStringAsync());
        var first = client.PostAsync(BridgeRoutes.TakeFrame, Json(new FrameRequest { TakeId = t.TakeId }));
        await factory.Worker.Entered.Task.WaitAsync(TestBudget.Hang);
        var waiting = Enumerable.Range(0, BridgeLimits.MaxQueuedRequests + 1).Select(_ => client.PostAsync(BridgeRoutes.TakeFrame,
            Json(new FrameRequest { TakeId = t.TakeId, FrameIndex = 1 }))).ToArray();
        try {
            using var refusal = await await Task.WhenAny(waiting).WaitAsync(TestBudget.Hang);
            Assert.Equal(BridgeErrors.Busy.Status, (int)refusal.StatusCode);
            using var health = await client.GetAsync(BridgeRoutes.Health); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        } finally { factory.Worker.Release.TrySetResult(); }
        using (var answer = await first) Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        foreach (var task in waiting) { using var r = await task; Assert.Contains((int)r.StatusCode, new[] { 200, BridgeErrors.Busy.Status }); }
        var status = DslJson.Deserialize<TakeSnapshot>(await client.GetStringAsync(BridgeRoutes.Take));
        Assert.Equal(2, status.Take!.CapturedFrames);
    }
    private static StringContent Json(object value) => new(DslJson.Serialize(value), Encoding.UTF8, "application/json");
    private sealed class Factory : IGodotWorkerFactory
    {
        public Worker Worker = new(); public CapabilityManifest Manifest => new GodotSessionTests.Factory().Manifest;
        public void AssertFresh() { }
        public Task<IGodotWorker> Open(CancellationToken ct) => Task.FromResult<IGodotWorker>(Worker);
    }
    private sealed class Worker : IGodotWorker
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int next;
        public Task Begin(CompiledTimeline timeline, int width, int height, CancellationToken ct) => Task.CompletedTask;
        public async Task<WorkerFrame> Frame(int index, int fps, bool advanceOnly, CancellationToken ct, bool measure = false) {
            Assert.Equal(next, index); if (index == 0) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); } next++;
            return new(new byte[] { (byte)index }); }
        public Task<JsonElement> Finish(CancellationToken ct) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
