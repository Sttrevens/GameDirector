using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using GameDirector.Core.Dsl;
using GameDirector.Core.Capture;
using System.Text.Json;

namespace GameDirector.Client
{
    /// <summary>
    /// Thin HTTP client for the in-game bridge (packages/com.gamedirector.unity).
    /// Protocol is intentionally boring JSON-over-HTTP so any host (CLI, MCP,
    /// CI, a second game engine's tooling) can drive a running game. Routes and
    /// the default endpoint come from GameDirector.Core's contract definitions,
    /// shared verbatim with the server side.
    public sealed class DirectorBridgeClient : IDisposable
    {
        private readonly HttpClient _http;

        public DirectorBridgeClient(string? endpoint = null, int timeoutSeconds = 30)
        {
            endpoint ??= BridgeDefaults.UnityEndpoint;
            _http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        }

        public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
        {
            try { return (await _http.GetAsync(BridgeRoutes.Health, ct)).IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        public async Task<CapabilityManifest> GetManifestAsync(CancellationToken ct = default) =>
            await _http.GetFromJsonAsync<CapabilityManifest>(BridgeRoutes.Manifest, DslJson.Options, ct)
            ?? throw new InvalidOperationException("bridge returned empty manifest");

        public async Task<string> PlayAsync(TimelineAsset timeline, CancellationToken ct = default)
        {
            var response = await _http.PostAsJsonAsync(BridgeRoutes.TimelinePlay, timeline, DslJson.Options, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"play rejected ({(int)response.StatusCode}): {body}");
            using var receipt=JsonDocument.Parse(body);
            if (!receipt.RootElement.TryGetProperty("ok",out var ok) || !ok.GetBoolean())
                throw new InvalidOperationException("play rejected: "+body);
            return body;
        }

        public async Task StopAsync(CancellationToken ct = default) =>
            (await _http.PostAsync(BridgeRoutes.TimelineStop, content: null, ct)).EnsureSuccessStatusCode();

        public async Task<string> GetStatusAsync(CancellationToken ct = default) =>
            await _http.GetStringAsync(BridgeRoutes.Status, ct);

        public async Task CaptureFrameAsync(string outPath, CancellationToken ct = default)
        {
            outPath = OutputPolicy.RequireOutput(outPath, OutputPolicy.ManifestRoot(await GetManifestAsync(ct)));
            var bytes = await _http.GetByteArrayAsync(BridgeRoutes.Capture, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            await File.WriteAllBytesAsync(outPath, bytes, ct);
        }

        public async Task<TakeReceipt> BeginTakeAsync(TakeRequest request,CancellationToken ct=default)
        {
            using var response=await _http.PostAsJsonAsync(BridgeRoutes.TakeStart,request,DslJson.Options,ct);
            var json=await response.Content.ReadAsStringAsync(ct);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException("take rejected: "+json);
            return DslJson.Deserialize<TakeReceipt>(json);
        }
        public async Task<byte[]> TakeFrameAsync(FrameRequest request,CancellationToken ct=default)
        {
            // Retry the SAME identity/index only. Never retry a start mutation.
            for(int attempt=0;;attempt++) {
                try {
                    using var response=await _http.PostAsJsonAsync(BridgeRoutes.TakeFrame,request,DslJson.Options,ct);
                    if(response.StatusCode==System.Net.HttpStatusCode.ServiceUnavailable || response.StatusCode==System.Net.HttpStatusCode.GatewayTimeout)
                        throw new HttpRequestException("transient frame timeout or queue saturation",null,response.StatusCode);
                    if(!response.IsSuccessStatusCode)throw new InvalidOperationException("frame rejected: "+await response.Content.ReadAsStringAsync(ct));
                    if(response.Content.Headers.ContentType?.MediaType!="image/png")throw new InvalidOperationException("expected PNG frame");
                    var bytes=await response.Content.ReadAsByteArrayAsync(ct);
                    if(bytes.Length<8 || bytes[0]!=137 || bytes[1]!=80 || bytes[2]!=78 || bytes[3]!=71)throw new InvalidOperationException("invalid PNG payload");
                    return bytes;
                } catch(Exception ex) when(attempt<2 && !ct.IsCancellationRequested && (ex is HttpRequestException || ex is TaskCanceledException)) {}
            }
        }
        public async Task CancelTakeAsync(string id,CancellationToken ct=default) {
            using var response=await _http.PostAsJsonAsync(BridgeRoutes.TakeStop,new FrameRequest{TakeId=id},DslJson.Options,ct);
            response.EnsureSuccessStatusCode();
        }
        public Task<string> GetTakeAsync(CancellationToken ct=default)=>_http.GetStringAsync(BridgeRoutes.Take,ct);

        public void Dispose() => _http.Dispose();
    }
}
