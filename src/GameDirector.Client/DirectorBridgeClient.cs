using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using GameDirector.Core.Dsl;

namespace GameDirector.Client
{
    /// <summary>
    /// Thin HTTP client for the in-game bridge (packages/com.gamedirector.unity).
    /// Protocol is intentionally boring JSON-over-HTTP so any host (CLI, MCP,
    /// CI, a second game engine's tooling) can drive a running game.
    ///
    /// Endpoints (bridge default http://127.0.0.1:39777):
    ///   GET  /health          -> { "ok": true, "state": "..." }
    ///   GET  /manifest        -> CapabilityManifest JSON
    ///   POST /timeline/play   -> body = TimelineAsset JSON; bridge compiles server-side
    ///   POST /timeline/stop   -> {}
    ///   GET  /status          -> { state, time, duration, lastEvents[] }
    ///   GET  /capture         -> PNG bytes (current director camera)
    /// </summary>
    public sealed class DirectorBridgeClient : IDisposable
    {
        private readonly HttpClient _http;

        public DirectorBridgeClient(string endpoint = "http://127.0.0.1:39777", int timeoutSeconds = 10)
        {
            _http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        }

        public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
        {
            try { return (await _http.GetAsync("health", ct)).IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        public async Task<CapabilityManifest> GetManifestAsync(CancellationToken ct = default) =>
            await _http.GetFromJsonAsync<CapabilityManifest>("manifest", DslJson.Options, ct)
            ?? throw new InvalidOperationException("bridge returned empty manifest");

        public async Task<string> PlayAsync(TimelineAsset timeline, CancellationToken ct = default)
        {
            var response = await _http.PostAsJsonAsync("timeline/play", timeline, DslJson.Options, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"play rejected ({(int)response.StatusCode}): {body}");
            return body;
        }

        public async Task StopAsync(CancellationToken ct = default) =>
            (await _http.PostAsync("timeline/stop", content: null, ct)).EnsureSuccessStatusCode();

        public async Task<string> GetStatusAsync(CancellationToken ct = default) =>
            await _http.GetStringAsync("status", ct);

        public async Task CaptureFrameAsync(string outPath, CancellationToken ct = default)
        {
            var bytes = await _http.GetByteArrayAsync("capture", ct);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            await File.WriteAllBytesAsync(outPath, bytes, ct);
        }

        public void Dispose() => _http.Dispose();
    }
}
