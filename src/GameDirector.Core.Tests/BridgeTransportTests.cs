using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public class BridgeTransportTests
{
    [Theory]
    [InlineData("health")]
    [InlineData("manifest")]
    [InlineData("play")]
    [InlineData("stop")]
    [InlineData("status")]
    [InlineData("start")]
    [InlineData("frame")]
    [InlineData("capture")]
    [InlineData("cancel")]
    [InlineData("take")]
    public async Task EveryRouteClassifiesHttpRejectionOnce(string route)
    {
        await using var server = new BridgeHttpFixture(ctx => BridgeHttpFixture.Reply(ctx, 400, "{\"error\":\"bad geometry\"}"));
        using var client = new DirectorBridgeClient(server.Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => Call(client, route));
        Assert.Equal(BridgeException.FailureKind.Rejected, error.Kind);
        Assert.Contains("bad geometry", error.Message);
        Assert.Equal(server.Endpoint, error.Endpoint);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("{\"error\":\"bad geometry\"}", error.Body);
    }

    [Theory] [InlineData("version")] [InlineData("duplicate")] [InlineData("capability")]
    public async Task ParsedInvalidEngineManifestIsAProtocolFaultNotCallerInput(string kind)
    {
        var manifest = TestAssets.InvalidManifest(kind);
        await using var server = new BridgeHttpFixture(ctx => BridgeHttpFixture.Reply(ctx, 200, DslJson.Serialize(manifest)));
        using var client = new DirectorBridgeClient(server.Endpoint);
        var ex = await Assert.ThrowsAsync<EngineProtocolException>(() => client.GetManifestAsync());
        Assert.Contains("manifest", ex.Message);
        Assert.Contains("GD", ex.Message);
    }

    private static async Task Call(DirectorBridgeClient client, string route)
    {
        switch (route)
        {
            case "health": await client.IsHealthyAsync(); break;
            case "manifest": await client.GetManifestAsync(); break;
            case "play": await client.PlayAsync(new TimelineAsset()); break;
            case "stop": await client.StopAsync(); break;
            case "status": await client.GetStatusAsync(); break;
            case "start": await client.BeginTakeAsync(new TakeRequest()); break;
            case "frame": await client.TakeFrameAsync(new FrameRequest { TakeId = "owner", FrameIndex = 3 }); break;
            case "capture": await client.CaptureFrameAsync(); break;
            case "cancel": await client.CancelTakeAsync("owner"); break;
            case "take": await client.GetTakeAsync(); break;
        }
    }

    [Fact]
    public async Task RefusedPortIsUnreachableEvenWithoutOperationsWrapper()
    {
        using var client = new DirectorBridgeClient(BridgeHttpFixture.UnusedEndpoint());
        var error = await Assert.ThrowsAsync<BridgeException>(() => client.GetStatusAsync());
        Assert.Equal(BridgeException.FailureKind.Unreachable, error.Kind);
    }

    [Fact]
    public async Task ClientBudgetIsTimeoutNotUnreachable()
    {
        await using var server = new BridgeHttpFixture(async ctx => { await Task.Delay(1600); await BridgeHttpFixture.Reply(ctx, 200, "{}"); }); // timing-under-test: the reply must outlast the client's 1 s budget, so a timeout is what remains
        using var client = new DirectorBridgeClient(server.Endpoint, timeoutSeconds: 1);
        var error = await Assert.ThrowsAsync<BridgeException>(() => client.GetTakeAsync());
        Assert.Equal(BridgeException.FailureKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task CallerCancellationIsNotABridgeFailure()
    {
        await using var server = new BridgeHttpFixture(async ctx => { await Task.Delay(1600); await BridgeHttpFixture.Reply(ctx, 200, "{}"); }); // timing-under-test: the reply must outlast the caller's 100 ms cancellation
        using var client = new DirectorBridgeClient(server.Endpoint);
        using var ct = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTakeAsync(ct.Token));
    }

    [Fact]
    public async Task BusyFramesBackOffAndRepeatOnlyTheSameIdentity()
    {
        var calls = new List<(long At, string Body)>(); var clock = Stopwatch.StartNew(); // timing-under-test: retry spacing is the subject, so the clock measures it
        await using var server = new BridgeHttpFixture(async ctx => {
            using var reader = new StreamReader(ctx.Request.InputStream);
            calls.Add((clock.ElapsedMilliseconds, await reader.ReadToEndAsync()));
            await BridgeHttpFixture.Reply(ctx, calls.Count < 4 ? 503 : 200,
                calls.Count < 4 ? Encoding.UTF8.GetBytes("{\"error\":\"queue full\"}") : BridgeHttpFixture.Png,
                calls.Count < 4 ? "application/json" : "image/png");
        });
        using var client = new DirectorBridgeClient(server.Endpoint);
        Assert.Equal(BridgeHttpFixture.Png, await client.TakeFrameAsync(new FrameRequest { TakeId = "same-owner", FrameIndex = 7 }));
        Assert.Equal(4, calls.Count);
        Assert.Single(calls.Select(c => c.Body).Distinct());
        Assert.Equal("same-owner", JsonNode.Parse(calls[0].Body)!["takeId"]!.GetValue<string>());
        Assert.Equal(7, JsonNode.Parse(calls[0].Body)!["frameIndex"]!.GetValue<int>());
        Assert.InRange(calls[1].At - calls[0].At, 220, 3000); // timing-under-test: the ≥220 ms backoff is the claim; the ceiling is slack, not a speed bound
        Assert.InRange(calls[2].At - calls[1].At, 470, 3500); // timing-under-test: the ≥470 ms doubled backoff is the claim
        Assert.InRange(calls[3].At - calls[2].At, 970, 4000); // timing-under-test: the ≥970 ms doubled backoff is the claim
    }

    [Fact]
    public async Task AdvanceOnlyRetriesKeepTheFlagAndAcceptOnlyAnEmpty204()
    {
        var calls = new List<string>(); var request = new FrameRequest { TakeId = "owner", FrameIndex = 2, AdvanceOnly = true };
        await using var server = new BridgeHttpFixture(async ctx => {
            using var reader = new StreamReader(ctx.Request.InputStream); calls.Add(await reader.ReadToEndAsync());
            if (calls.Count == 1) { request.AdvanceOnly = false; await BridgeHttpFixture.Reply(ctx, 503, "{}"); }
            else await BridgeHttpFixture.Reply(ctx, 204, "");
        });
        using var client = new DirectorBridgeClient(server.Endpoint);
        Assert.Empty(await client.TakeFrameAsync(request)); Assert.Equal(2, calls.Count); Assert.Equal(calls[0], calls[1]);
        Assert.True(JsonNode.Parse(calls[0])!["advanceOnly"]!.GetValue<bool>());
    }

    [Theory] [InlineData(true, 200)] [InlineData(false, 204)]
    public async Task FrameResponseMustMatchTheRequestedMode(bool advance, int status)
    {
        await using var server = new BridgeHttpFixture(ctx => BridgeHttpFixture.Reply(ctx, status, ""));
        using var client = new DirectorBridgeClient(server.Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => client.TakeFrameAsync(new FrameRequest { TakeId = "owner", AdvanceOnly = advance }));
        Assert.Equal(BridgeException.FailureKind.Rejected, error.Kind);
    }

    [Theory]
    [InlineData(400, "Rejected")]
    [InlineData(409, "Rejected")]
    [InlineData(500, "Rejected")]
    [InlineData(429, "Busy")]
    [InlineData(408, "Timeout")]
    [InlineData(503, "Busy")]
    [InlineData(504, "Timeout")]
    public async Task StartNeverRetriesEvenTransientStatuses(int status, string kind)
    {
        var calls = 0;
        await using var server = new BridgeHttpFixture(ctx => { calls++; return BridgeHttpFixture.Reply(ctx, status, "{\"error\":\"refused\"}"); });
        using var client = new DirectorBridgeClient(server.Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => client.BeginTakeAsync(new TakeRequest()));
        Assert.Equal(kind, error.Kind.ToString()); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FrameRetriesExhaustWithTheFinalStatusAndBody()
    {
        var calls = 0;
        await using var server = new BridgeHttpFixture(ctx => { calls++; return BridgeHttpFixture.Reply(ctx, 503, "{\"error\":\"still busy\"}"); });
        using var client = new DirectorBridgeClient(server.Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => client.TakeFrameAsync(new FrameRequest { TakeId = "owner", FrameIndex = 4 }));
        Assert.Equal(4, calls); Assert.Equal(BridgeException.FailureKind.Busy, error.Kind);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode); Assert.Equal("{\"error\":\"still busy\"}", error.Body);
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(409, "state_conflict")]
    [InlineData(500, "internal")]
    public async Task RejectedFramesKeepTheCodeAndAreNeverRetried(int status, string code)
    {
        var calls = 0; var body = DslJson.Serialize(new { error = "fixture diagnosis", code });
        await using var server = new BridgeHttpFixture(ctx => { calls++; return BridgeHttpFixture.Reply(ctx, status, body); });
        using var client = new DirectorBridgeClient(server.Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => client.TakeFrameAsync(new FrameRequest { TakeId = "owner", FrameIndex = 4 }));
        Assert.Equal(BridgeException.FailureKind.Rejected, error.Kind); Assert.Equal(1, calls);
        Assert.Equal(code, typeof(BridgeException).GetProperty("Code")?.GetValue(error));
    }

    [Fact] public async Task StructuralFrameSnapshotCarriesEverySettablePropertyThroughRetries()
    {
        var request = new FrameRequest();
        var properties = typeof(FrameRequest).GetProperties().Where(p => p.SetMethod?.IsPublic == true).ToArray();
        foreach (var p in properties) p.SetValue(request, p.PropertyType == typeof(string) ? "owner" : p.PropertyType == typeof(int) ? 7 :
            p.PropertyType == typeof(bool) ? true : throw new Exception("Supply a non-default fixture value for " + p.Name));
        var snapshot = typeof(FrameRequest).GetMethod("Snapshot"); Assert.NotNull(snapshot);
        var copy = Assert.IsType<FrameRequest>(snapshot!.Invoke(request, null)); Assert.NotSame(request, copy);
        var expected = JsonNode.Parse(DslJson.Serialize(request))!.AsObject();
        var calls = new List<string>();
        var handler = new FrameHandler(async message => {
            calls.Add(await message.Content!.ReadAsStringAsync());
            foreach (var p in properties) p.SetValue(request, p.PropertyType.IsValueType ? Activator.CreateInstance(p.PropertyType) : null);
            return new HttpResponseMessage(calls.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        });
        var ctor = typeof(DirectorBridgeClient).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(HttpMessageHandler) }, null); Assert.NotNull(ctor);
        using var client = (DirectorBridgeClient)ctor!.Invoke(new object[] { "http://127.0.0.1:1", handler });
        Assert.Empty(await client.TakeFrameAsync(request)); Assert.Equal(2, calls.Count); Assert.Equal(calls[0], calls[1]);
        var sent = JsonNode.Parse(calls[0])!.AsObject(); Assert.Equal(properties.Length, sent.Count);
        foreach (var p in properties) {
            var name = DslJson.Options.PropertyNamingPolicy!.ConvertName(p.Name);
            Assert.True(JsonNode.DeepEquals(expected[name], sent[name]), "dropped request field: " + p.Name);
        }
    }
    private sealed class FrameHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request);
    }

    [Fact] public async Task StopSendsOnlyItsOwnIdentityShape()
    {
        var stop = typeof(FrameRequest).Assembly.GetType("GameDirector.Core.Capture.TakeStopRequest"); Assert.NotNull(stop);
        Assert.Equal(new[] { "TakeId" }, stop!.GetProperties().Select(p => p.Name)); Assert.NotNull(stop.GetMethod("Snapshot"));
        Assert.NotNull(typeof(TakeRequest).GetMethod("Snapshot"));
        string? sent = null;
        await using var server = new BridgeHttpFixture(async ctx => {
            using var reader = new StreamReader(ctx.Request.InputStream); sent = await reader.ReadToEndAsync();
            Assert.Equal("/take/stop", ctx.Request.Url!.AbsolutePath); await BridgeHttpFixture.Reply(ctx, 200, "{}");
        });
        using var client = new DirectorBridgeClient(server.Endpoint); await client.CancelTakeAsync("owner");
        Assert.Equal(new[] { "takeId" }, JsonNode.Parse(sent!)!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task CancellationInterruptsBackoffWithoutReplayingTheFrame()
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        await using var server = new BridgeHttpFixture(async ctx => { calls++; await BridgeHttpFixture.Reply(ctx, 503, "{}"); answered.SetResult(); });
        using var client = new DirectorBridgeClient(server.Endpoint); using var ct = new CancellationTokenSource();
        var frame = client.TakeFrameAsync(new FrameRequest { TakeId = "owner" }, ct.Token);
        await answered.Task; ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frame); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallerCannotChangeTheIdentityDuringBackoff()
    {
        var calls = 0; var request = new FrameRequest { TakeId = "owner", FrameIndex = 4 };
        await using var server = new BridgeHttpFixture(async ctx => {
            using var reader = new StreamReader(ctx.Request.InputStream); var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Equal("owner", body["takeId"]!.GetValue<string>()); Assert.Equal(4, body["frameIndex"]!.GetValue<int>());
            if (++calls == 1) { request.TakeId = "different"; request.FrameIndex = 99; await BridgeHttpFixture.Reply(ctx, 503, "{}"); }
            else await BridgeHttpFixture.Reply(ctx, 200, BridgeHttpFixture.Png, "image/png");
        });
        using var client = new DirectorBridgeClient(server.Endpoint);
        Assert.Equal(BridgeHttpFixture.Png, await client.TakeFrameAsync(request)); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StartHasItsOwnBudgetBeyondTheOrdinaryRequestTimeout()
    {
        await using var server = new BridgeHttpFixture(async ctx => { await Task.Delay(1300); await BridgeHttpFixture.Reply(ctx, 200, "{\"takeId\":\"slow-start\",\"state\":\"Capturing\"}"); }); // timing-under-test: slower than the 1 s request budget, so take-start must use its own longer one
        using var client = new DirectorBridgeClient(server.Endpoint, timeoutSeconds: 1);
        Assert.Equal("slow-start", (await client.BeginTakeAsync(new TakeRequest())).TakeId);
    }

    [Theory]
    [InlineData("take", "{broken")]
    [InlineData("play", "{\"ok\":false,\"error\":\"bad cue\"}")]
    [InlineData("frame", "not png")]
    public async Task MalformedOrNegativeSuccessRepliesAreRejectedWithoutRetry(string route, string body)
    {
        var calls = 0;
        await using var server = new BridgeHttpFixture(ctx => { calls++; return BridgeHttpFixture.Reply(ctx, 200, body); });
        using var client = new DirectorBridgeClient(server.Endpoint);
        var error = await Assert.ThrowsAsync<BridgeException>(() => Call(client, route));
        Assert.Equal(BridgeException.FailureKind.Rejected, error.Kind); Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(body, error.Body);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptAndStatusAreTypedForBothEngines(bool three)
    {
        const string receipt = "{\"takeId\":\"receipt-id\",\"timelineId\":\"film\",\"state\":\"Completed\",\"frameRate\":24,\"width\":1280,\"height\":720,\"frameCount\":48,\"capturedFrames\":48,\"audio\":\"silent-picture; mix audio in the edit\"}";
        var events = three ? "[{\"t\":0,\"type\":\"camera.shot\",\"role\":\"hero\"}]" : "[{\"time\":0,\"dispatchedAt\":0,\"cueIndex\":0,\"type\":\"camera.shot\",\"summary\":\"shot\"}]";
        var json = "{\"take\":" + receipt + ",\"events\":" + events + ",\"reviews\":[{\"frame\":0,\"time\":0,\"composition\":{\"ok\":true}}]}";
        await using var server = new BridgeHttpFixture(ctx => BridgeHttpFixture.Reply(ctx, 200,
            ctx.Request.Url!.AbsolutePath == "/status" && !three
                ? "{\"state\":\"Finished\",\"time\":2,\"duration\":2,\"take\":" + receipt + ",\"recentEvents\":" + events + "}" : json));
        using var client = new DirectorBridgeClient(server.Endpoint);
        object take = await client.GetTakeAsync(); object status = await client.GetStatusAsync();
        Assert.IsNotType<string>(take); Assert.IsNotType<string>(status);
        var parsed = JsonNode.Parse(DslJson.Serialize(take))!;
        Assert.Equal("receipt-id", parsed["take"]!["takeId"]!.GetValue<string>());
        Assert.Equal(48, parsed["take"]!["capturedFrames"]!.GetValue<int>());
        Assert.Equal("camera.shot", parsed["events"]![0]!["type"]!.GetValue<string>());
        Assert.True(parsed["reviews"]![0]!["composition"]!["ok"]!.GetValue<bool>());
        var playback = JsonNode.Parse(DslJson.Serialize(status))!;
        Assert.Equal("receipt-id", playback["take"]!["takeId"]!.GetValue<string>());
        Assert.Equal("camera.shot", playback[three ? "events" : "recentEvents"]![0]!["type"]!.GetValue<string>());
        var typed = Assert.IsType<TakeSnapshot>(take);
        Assert.Equal("camera.shot", Assert.Single(typed.Events!).Type);
        Assert.Equal(24, typed.Take!.FrameRate); Assert.Equal(48, typed.Take.FrameCount);
    }
}

// Real loopback HTTP, not a handler mock: classifications include socket refusal,
// HTTP body/status, content validation and elapsed transport time.
internal sealed class BridgeHttpFixture : IAsyncDisposable
{
    public static readonly byte[] Png = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private readonly LoopbackHttp server = LoopbackHttp.Start();
    public string Endpoint => server.Endpoint;
    private readonly Task loop;
    private readonly List<Task> requests = new();
    public BridgeHttpFixture(Func<HttpListenerContext, Task> respond)
    {
        var listener = server.Listener;
        loop = Task.Run(async () => {
            try { while (true) {
                var ctx = await listener.GetContextAsync();
                requests.Add(Task.Run(async () => { try { await respond(ctx); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { } finally { ctx.Response.Close(); } }));
            }} catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        });
    }
    // Deliberately an endpoint where NOTHING listens (connection refused): the
    // pick-then-release gap is the fixture, not a race, so this stays as-is.
    public static string UnusedEndpoint()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        var endpoint = "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return endpoint;
    }
    public static Task Reply(HttpListenerContext ctx, int status, string body) => Reply(ctx, status, Encoding.UTF8.GetBytes(body), "application/json");
    public static async Task Reply(HttpListenerContext ctx, int status, byte[] body, string type)
    {
        ctx.Response.StatusCode = status; ctx.Response.ContentType = type; ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
    }
    public async ValueTask DisposeAsync() { server.Dispose(); await loop; await Task.WhenAll(requests); }
}
