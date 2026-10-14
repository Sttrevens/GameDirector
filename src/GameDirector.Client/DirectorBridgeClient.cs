using System.Net;
using System.Text;
using System.Text.Json;
using GameDirector.Core.Dsl;
using GameDirector.Core.Capture;

namespace GameDirector.Client;

/// <summary>JSON-over-HTTP bridge transport. Every answering failure keeps its
/// status/body; only a failed connection is Unreachable. Files belong to callers.</summary>
public sealed class DirectorBridgeClient : IDisposable
{
    private readonly HttpClient http;
    public string Endpoint { get; }
    public int TimeoutSeconds { get; }
    public int TakeStartTimeoutSeconds { get; }
    public DirectorBridgeClient(string? endpoint = null, int timeoutSeconds = BridgeDefaults.RequestTimeoutSeconds,
        int takeStartTimeoutSeconds = BridgeDefaults.TakeStartTimeoutSeconds)
    {
        Endpoint = endpoint ?? BridgeDefaults.UnityEndpoint;
        TimeoutSeconds = timeoutSeconds; TakeStartTimeoutSeconds = takeStartTimeoutSeconds;
        http = new HttpClient { BaseAddress = new Uri(Endpoint.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }
    internal DirectorBridgeClient(string endpoint, HttpMessageHandler handler) : this(endpoint) {
        http.Dispose(); http = new HttpClient(handler) { BaseAddress = new Uri(Endpoint.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }
    private sealed record Reply(HttpStatusCode Status, byte[] Bytes, string? ContentType)
    {
        public string Body => Encoding.UTF8.GetString(Bytes);
    }
    // A per-call budget covers headers AND the body; it is independent of the
    // caller's cancellation, which must still propagate unclassified.
    private async Task<Reply> Send(string route, object? input, bool post, CancellationToken ct, int? budget = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seconds = budget ?? TimeoutSeconds; timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        HttpStatusCode? status = null;
        try {
            using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, route);
            if (input != null) request.Content = new StringContent(DslJson.Serialize(input), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = response.StatusCode;
            var reply = new Reply(response.StatusCode, await response.Content.ReadAsByteArrayAsync(timeout.Token), response.Content.Headers.ContentType?.MediaType);
            ct.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) throw BridgeException.Answered(Endpoint, reply.Status, reply.Body);
            return reply;
        }
        catch (HttpRequestException ex) {
            // No successful headers: connection/DNS/socket failure. A response
            // that broke during delivery is a rejected/malformed answer instead.
            if (status == null && ex.StatusCode == null) throw BridgeException.Unreachable(Endpoint, ex);
            throw BridgeException.Answered(Endpoint, status ?? ex.StatusCode!.Value, "", "response delivery failed", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw BridgeException.TimedOut(Endpoint, seconds, ex, status); }
    }
    private T Parse<T>(Reply reply)
    {
        try { return DslJson.Deserialize<T>(reply.Body); }
        catch (JsonException ex) { throw BridgeException.Answered(Endpoint, reply.Status, reply.Body, "invalid " + typeof(T).Name, ex); }
    }
    private byte[] Png(Reply reply)
    {
        var b = reply.Bytes;
        if (reply.ContentType != "image/png" || b.Length < 8 || b[0] != 137 || b[1] != 80 || b[2] != 78 || b[3] != 71 || b[4] != 13 || b[5] != 10 || b[6] != 26 || b[7] != 10)
            throw BridgeException.Answered(Endpoint, reply.Status, reply.Body, "expected PNG frame");
        return b;
    }
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default) { await Send(BridgeRoutes.Health, null, false, ct); return true; }
    public async Task<CapabilityManifest> GetManifestAsync(CancellationToken ct = default)
    {
        var manifest = Parse<CapabilityManifest>(await Send(BridgeRoutes.Manifest, null, false, ct));
        DirectorOperations.RequireValidManifest(manifest);
        return manifest;
    }
    public async Task<string> PlayAsync(TimelineAsset timeline, CancellationToken ct = default)
    {
        var reply = await Send(BridgeRoutes.TimelinePlay, timeline, true, ct);
        var receipt = Parse<JsonElement>(reply);
        if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw BridgeException.Answered(Endpoint, reply.Status, reply.Body);
        return reply.Body;
    }
    public async Task StopAsync(CancellationToken ct = default) => await Send(BridgeRoutes.TimelineStop, null, true, ct);
    public async Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default) => Parse<StatusSnapshot>(await Send(BridgeRoutes.Status, null, false, ct));
    public async Task<byte[]> CaptureFrameAsync(CancellationToken ct = default) => Png(await Send(BridgeRoutes.Capture, null, false, ct));
    public async Task<TakeReceipt> BeginTakeAsync(TakeRequest request, CancellationToken ct = default) =>
        Parse<TakeReceipt>(await Send(BridgeRoutes.TakeStart, request.Snapshot(), true, ct, TakeStartTimeoutSeconds));
    public async Task<byte[]> TakeFrameAsync(FrameRequest request, CancellationToken ct = default)
    {
        // Snapshot identity/index before the first await: retries cannot follow a
        // caller mutating its request object. Start mutations are never retried.
        var frame = request.Snapshot();
        for (int attempt = 0;; attempt++) {
            try {
                var reply = await Send(BridgeRoutes.TakeFrame, frame, true, ct);
                if (!frame.AdvanceOnly) return Png(reply);
                if (reply.Status != HttpStatusCode.NoContent || reply.Bytes.Length != 0)
                    throw BridgeException.Answered(Endpoint, reply.Status, reply.Body, "expected empty 204 for advance-only frame");
                return reply.Bytes;
            }
            catch (BridgeException ex) {
                ct.ThrowIfCancellationRequested();
                if (attempt >= BridgeDefaults.FrameRetryCount || ex.Kind is not (BridgeException.FailureKind.Busy or BridgeException.FailureKind.Timeout or BridgeException.FailureKind.Unreachable)) throw;
                await Task.Delay(Math.Min(BridgeDefaults.FrameRetryMaxDelayMs, BridgeDefaults.FrameRetryInitialDelayMs * (1 << attempt)), ct);
            }
        }
    }
    public async Task CancelTakeAsync(string id, CancellationToken ct = default) => await Send(BridgeRoutes.TakeStop, new TakeStopRequest { TakeId = id }, true, ct);
    public async Task<TakeSnapshot> GetTakeAsync(CancellationToken ct = default) => Parse<TakeSnapshot>(await Send(BridgeRoutes.Take, null, false, ct));
    public void Dispose() => http.Dispose();
}
