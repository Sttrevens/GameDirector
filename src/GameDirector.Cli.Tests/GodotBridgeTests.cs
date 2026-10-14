using System.Net;
using System.Text;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using GameDirector.Godot;
using Xunit;

namespace GameDirector.Cli.Tests;

public sealed class GodotBridgeTests
{
    // Catches bypassing HTTP security/body admission or routing to arbitrary stage mutations.
    [Fact]
    public async Task Routes_security_headers_media_type_and_json_object_ceiling()
    {
        await using var bridge = await GodotBridge.Start(new GodotSessionTests.Factory(), 0);
        using var client = new HttpClient { BaseAddress = new(bridge.Endpoint) };
        foreach (var route in new[] { BridgeRoutes.Health, BridgeRoutes.Manifest, BridgeRoutes.Contract, BridgeRoutes.Take, BridgeRoutes.Status }) {
            using var r = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
            Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        }
        using (var r = await client.GetAsync("missing")) Assert.Equal(BridgeErrors.NotFound.Status, (int)r.StatusCode);
        using (var r = await client.SendAsync(new(HttpMethod.Put, BridgeRoutes.Health))) Assert.Equal(BridgeErrors.MethodNotAllowed.Status, (int)r.StatusCode);
        foreach (var (header, value, method) in new[] { ("Sec-Fetch-Site", "cross-site", HttpMethod.Get), ("Host", "evil.invalid", HttpMethod.Get), ("Origin", "https://evil.invalid", HttpMethod.Post) }) {
            using var q = new HttpRequestMessage(method, BridgeRoutes.TakeStart); q.Headers.Add(header, value);
            using var r = await client.SendAsync(q); Assert.Equal(BridgeErrors.Forbidden.Status, (int)r.StatusCode);
        }
        using (var r = await client.PostAsync(BridgeRoutes.TakeStart, new StringContent("{}"))) Assert.Equal(BridgeErrors.UnsupportedMediaType.Status, (int)r.StatusCode);
        foreach (var json in new[] { "[]", "null", "{", "{}", "{\"frameRate\":2.5}", "{\"frameRate\":\"2\",\"width\":64,\"height\":64,\"timeline\":{}}", "{\"frameRate\":null,\"width\":64,\"height\":64,\"timeline\":{}}" }) {
            using var r = await client.PostAsync(BridgeRoutes.TakeStart, Json(json)); Assert.Equal(BridgeErrors.InvalidRequest.Status, (int)r.StatusCode);
        }
        using (var r = await client.PostAsync(BridgeRoutes.TakeStart, Json(new string(' ', BridgeLimits.MaxRequestBytes + 1))))
            Assert.Equal(BridgeErrors.PayloadTooLarge.Status, (int)r.StatusCode);
    }

    [Fact]
    public async Task Capture_is_busy_for_other_mutations_but_inspection_and_frame_retries_work()
    {
        await using var bridge = await GodotBridge.Start(new GodotSessionTests.Factory(), 0);
        using var client = new HttpClient { BaseAddress = new(bridge.Endpoint) };
        var request = new TakeRequest { FrameRate = 2, Width = 64, Height = 64, Timeline = new() { Id = "fixture", Cues = new() {
            new() { Type = CueTypes.CameraShot, Shot = new() { Subject = "hero", From = "anchor", DurationSeconds = 2 } } } } };
        using var started = await client.PostAsync(BridgeRoutes.TakeStart, Json(DslJson.Serialize(request)));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var t = DslJson.Deserialize<TakeReceipt>(await started.Content.ReadAsStringAsync());
        using (var r = await client.PostAsync(BridgeRoutes.TimelinePlay, Json("{}"))) Assert.Equal(BridgeErrors.StateConflict.Status, (int)r.StatusCode);
        using (var r = await client.PostAsync(BridgeRoutes.TakeStart, Json(DslJson.Serialize(request)))) Assert.Equal(BridgeErrors.StateConflict.Status, (int)r.StatusCode);
        using (var r = await client.GetAsync(BridgeRoutes.Manifest)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        for (var n = 0; n < 2; n++) {
            using var r = await client.PostAsync(BridgeRoutes.TakeFrame, Json(DslJson.Serialize(new FrameRequest { TakeId = t.TakeId })));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode); Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 0 }, await r.Content.ReadAsByteArrayAsync());
        }
        using var stop = await client.PostAsync(BridgeRoutes.TakeStop, Json(DslJson.Serialize(new TakeStopRequest { TakeId = t.TakeId })));
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
    }
    // Catches falling through to mutation routes, or hiding the engine-specific refusal.
    [Theory]
    [InlineData(BridgeRoutes.TimelinePlay)]
    [InlineData(BridgeRoutes.TimelineStop)]
    [InlineData(BridgeRoutes.Capture)]
    public async Task Interactive_routes_refuse_without_opening_a_worker(string route)
    {
        await using var bridge = await GodotBridge.Start(new GodotSessionTests.Factory(), 0);
        using var client = new HttpClient { BaseAddress = new(bridge.Endpoint) };
        using var response = await client.PostAsync(route, null).WaitAsync(TestBudget.Hang);
        Assert.Equal(BridgeErrors.StateConflict.Status, (int)response.StatusCode);
        Assert.Contains("unsupported on Godot", await response.Content.ReadAsStringAsync());
        using var status = await client.GetAsync(BridgeRoutes.Status);
        using var snapshot = System.Text.Json.JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.False(snapshot.RootElement.TryGetProperty("take", out _));
    }
    // Catches accepting a stale PID record without proof from the particular owned supervisor.
    [Fact]
    public async Task Dock_recovery_proves_instance_without_disclosing_owner_token()
    {
        var owner = new GodotDockOwner("fixture-secret", "/fixture/project");
        await using var bridge = await GodotBridge.Start(new GodotSessionTests.Factory(), 0, dockOwner: owner);
        using var client = new HttpClient { BaseAddress = new(bridge.Endpoint) };
        var expected = Proof("fixture-secret", "abc", Environment.ProcessId, "/fixture/project");
        var body = await client.GetStringAsync(GodotDefaults.OwnerRoute + "?challenge=abc&proof=" + expected);
        using var response = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(expected, response.RootElement.GetProperty("proof").GetString());
        Assert.Equal(Environment.ProcessId, response.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("/fixture/project", response.RootElement.GetProperty("project").GetString());
        Assert.DoesNotContain("fixture-secret", body);
        Assert.NotEqual(body, await client.GetStringAsync(GodotDefaults.OwnerRoute + "?challenge=different&proof=" + Proof("fixture-secret", "different", Environment.ProcessId, "/fixture/project")));
        var contract = await client.GetStringAsync(BridgeRoutes.Contract);
        Assert.DoesNotContain(GodotDefaults.OwnerRoute, contract);
        Assert.DoesNotContain("fixture-secret", contract);
        await using var unowned = await GodotBridge.Start(new GodotSessionTests.Factory(), 0);
        using var other = new HttpClient { BaseAddress = new(unowned.Endpoint) };
        using var missing = await other.GetAsync(GodotDefaults.OwnerRoute + "?challenge=abc");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
    // Catches removing request authentication, identity binding, or browser protections on the recovery route.
    [Fact]
    public async Task Dock_owner_rejects_wrong_secret_pid_project_and_browser_headers()
    {
        await using var bridge = await GodotBridge.Start(new GodotSessionTests.Factory(), 0,
            dockOwner: new GodotDockOwner("fixture-secret", "/fixture/project"));
        using var client = new HttpClient { BaseAddress = new(bridge.Endpoint) };
        foreach (var proof in new[] { "", Proof("wrong-secret", "abc", Environment.ProcessId, "/fixture/project"),
            Proof("fixture-secret", "abc", Environment.ProcessId + 1, "/fixture/project"),
            Proof("fixture-secret", "abc", Environment.ProcessId, "/other/project") }) {
            using var r = await client.GetAsync(GodotDefaults.OwnerRoute + "?challenge=abc&proof=" + proof);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        var route = GodotDefaults.OwnerRoute + "?challenge=abc&proof=" + Proof("fixture-secret", "abc", Environment.ProcessId, "/fixture/project");
        foreach (var (header, value) in new[] { ("Host", "evil.invalid"), ("Sec-Fetch-Site", "cross-site"), ("Origin", "http://evil.invalid") }) {
            using var q = new HttpRequestMessage(HttpMethod.Get, route); q.Headers.Add(header, value);
            using var r = await client.SendAsync(q);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        foreach (var site in new[] { "none", "same-origin" }) {
            using var q = new HttpRequestMessage(HttpMethod.Get, route); q.Headers.Add("Sec-Fetch-Site", site);
            using var r = await client.SendAsync(q); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        // Kestrel is reachable on loopback only (not a wildcard/all-interface listener).
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(bridge.Endpoint).Host)));
    }
    internal static string Proof(string token, string challenge, int pid, string project) =>
        Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes($"{challenge}\n{pid}\n{project}"))).ToLowerInvariant();
    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
}
