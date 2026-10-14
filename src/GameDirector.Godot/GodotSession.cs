using System.Text.Json;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

public sealed class GodotSession(IGodotWorkerFactory factory, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private IGodotWorker? worker;
    private byte[]? lastFrame;
    private int next;
    private DateTimeOffset lastRequest;
    private readonly List<JsonElement> reviews = new(), poses = new(), measures = new();
    private JsonElement? events;
    public TakeReceipt? Take { get; private set; }
    public object Snapshot() => new { take = Take, events, reviews, poses, measures };
    public bool Capturing => Take?.State == TakeStates.Capturing;
    public CapabilityManifest Manifest => factory.Manifest;
    public async Task<TakeReceipt> Start(TakeRequest request, CancellationToken ct)
    {
        if (Capturing) throw new BridgeRequestException(BridgeErrors.StateConflict, "take already active");
        var m = Manifest;
        AdmissionResult admission;
        try { admission = await Task.Run(() => TakeAdmission.Admit(request.Timeline, m, request.FrameRate, request.Width, request.Height, request.CaptureFromFrame), ct)
            .WaitAsync(TimeSpan.FromSeconds(BridgeBudgets.CompileSeconds), ct); }
        catch (TimeoutException) { throw new BridgeRequestException(BridgeErrors.Timeout, "Core admission deadline exceeded"); }
        if (admission.ManifestValidation.HasErrors) throw new InvalidOperationException("invalid engine manifest: " + string.Join("; ", admission.ManifestValidation.Diagnostics));
        if (!admission.Ok) throw new BridgeRequestException(BridgeErrors.InvalidRequest, string.Join("; ", admission.Errors));
        factory.AssertFresh();
        if (!string.IsNullOrEmpty(request.SourceFingerprint) && request.SourceFingerprint != CapabilitySpecs.Declared(m, CapabilityKeys.PresentationSourceFingerprint))
            throw new BridgeRequestException(BridgeErrors.StateConflict, "source fingerprint changed");
        await Close();
        try {
            worker = await factory.Open(ct);
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct); ready.CancelAfter(TimeSpan.FromSeconds(BridgeBudgets.StageReadySeconds));
            await worker.Begin(admission.Compiled.Timeline, request.Width, request.Height, ready.Token);
            factory.AssertFresh();
        } catch { await Close(); throw; }
        next = 0; lastFrame = null; reviews.Clear(); poses.Clear(); measures.Clear(); events = null; lastRequest = clock.GetUtcNow();
        return Take = new() { TakeId = Guid.NewGuid().ToString("N"), TimelineId = admission.Compiled.Timeline.Id, State = TakeStates.Capturing,
            FrameRate = request.FrameRate, Width = request.Width, Height = request.Height, FrameCount = admission.FrameCount, CaptureFromFrame = request.CaptureFromFrame };
    }
    public async Task<byte[]?> Frame(FrameRequest request, CancellationToken ct)
    {
        var t = Identity(request.TakeId);
        if (request.Measure && request.AdvanceOnly) throw new BridgeRequestException(BridgeErrors.InvalidRequest, "measure is valid only on rendered frames");
        if (request.Measure && CapabilitySpecs.Declared(Manifest, CapabilityKeys.Measure) != CapabilityKeys.MeasureV1)
            throw new BridgeRequestException(BridgeErrors.StateConflict, "this Godot stage does not declare measure");
        if (request.FrameIndex < 0 || request.FrameIndex >= t.FrameCount || request.AdvanceOnly != (request.FrameIndex < t.CaptureFromFrame))
            throw new BridgeRequestException(BridgeErrors.StateConflict, "frame mode/index mismatch");
        if (next > 0 && request.FrameIndex == next - 1) return lastFrame;
        if (!Capturing || request.FrameIndex != next) throw new BridgeRequestException(BridgeErrors.StateConflict, "frame index mismatch; expected " + next);
        try {
            var result = await worker!.Frame(next, t.FrameRate, request.AdvanceOnly, ct, request.Measure);
            if (!request.AdvanceOnly && (result.Png == null || result.Png.Length == 0)) throw new InvalidOperationException("worker returned no PNG");
            lastFrame = result.Png; next++;
            if (!request.AdvanceOnly) t.CapturedFrames++;
            if (result.Review is { } review) reviews.Add(review);
            if (result.Pose is { } pose) poses.Add(pose);
            if (request.Measure) {
                if (result.Measure is not { } row) throw new InvalidOperationException("worker returned no measure row");
                measures.Add(row);
            }
            if (next == t.FrameCount) { events = await worker.Finish(ct); t.State = TakeStates.Completed; await Close(); }
            return lastFrame;
        } catch (Exception ex) { t.State = TakeStates.Failed; t.Error = ex.Message; await Close(); throw; }
    }
    private TakeReceipt Identity(string id)
    {
        if (Take == null || id != Take.TakeId) throw new BridgeRequestException(BridgeErrors.StateConflict, "take identity mismatch");
        lastRequest = clock.GetUtcNow(); return Take;
    }
    public async Task Stop(TakeStopRequest request)
    {
        Identity(request.TakeId); if (Capturing) Take!.State = TakeStates.Cancelled; await Close();
    }
    public async Task ExpireLease()
    {
        if (Capturing && (clock.GetUtcNow() - lastRequest).TotalSeconds > CaptureContract.IdleLeaseSeconds) {
            Take!.State = TakeStates.Cancelled; await Close();
        }
    }
    private async Task Close() { var owned = worker; worker = null; if (owned != null) await owned.DisposeAsync(); }
    public async ValueTask DisposeAsync() { if (Capturing) Take!.State = TakeStates.Cancelled; await Close(); }
}
