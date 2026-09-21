using System.Net;
using GameDirector.Decisions;

namespace GameDirector.Decisions.Tests;

/// <summary>Provider HTTP policy: official endpoint/model defaults, endpoint
/// validation, single bounded attempt, no redirects, timeout, size caps and
/// the local-path payload guard.</summary>
public class OpenRouterJevProviderTests
{
    private static readonly DecisionProviderSettings Settings = new("https://openrouter.ai/api/alpha/decisions", "typesafe/jev-1.13", "test-key");

    private static HttpResponseMessage OfficialOk() => TestRig.Json("""
        { "answers": { "cam": { "type": "choice", "choice": "cand-a", "confidence": 0.9, "probabilities": { "cand-a": 0.9, "cand-b": 0.1, "none": 0 } } } }
        """);

    [Fact]
    public async Task SendsOfficialRequestShape()
    {
        var handler = new TestRig.RecordingHandler(OfficialOk());
        var provider = new OpenRouterJevProvider(Settings, handler);
        var answers = await provider.Decide(TestRig.SampleRequest(), CancellationToken.None);
        Assert.Equal("cand-a", answers.Single().Choice);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.LastBody!)!;
        Assert.Equal("typesafe/jev-1.13", body["model"]!.GetValue<string>());
        Assert.NotNull(body["questions"]!["cam"]!["criteria"]![DecisionPolicyBounds.NoneCandidateId]);
    }

    [Fact]
    public async Task OmitsAuthorizationWithoutKey()
    {
        var handler = new TestRig.RecordingHandler(OfficialOk());
        var provider = new OpenRouterJevProvider(Settings with { ApiKey = "" }, handler);
        await provider.Decide(TestRig.SampleRequest(), CancellationToken.None);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public void RejectsPlainHttpBeyondLoopback()
    {
        Assert.Throws<ArgumentException>(() => new OpenRouterJevProvider(Settings with { Endpoint = "http://openrouter.ai/api/alpha/decisions" }));
        Assert.Throws<ArgumentException>(() => new OpenRouterJevProvider(Settings with { Endpoint = "https://openrouter.ai/api/alpha/decisions?x=1" }));
        Assert.Throws<ArgumentException>(() => new OpenRouterJevProvider(Settings with { Endpoint = "not-a-url" }));
        // Loopback HTTP is allowed for a local decision server.
        _ = new OpenRouterJevProvider(Settings with { Endpoint = "http://127.0.0.1:1234/decisions" });
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var handler = new TestRig.RecordingHandler(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/decisions") } });
        var provider = new OpenRouterJevProvider(Settings, handler);
        var ex = await Assert.ThrowsAsync<DecisionProviderException>(() => provider.Decide(TestRig.SampleRequest(), CancellationToken.None));
        Assert.Contains("redirect", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SingleBoundedAttemptOnServerFailure()
    {
        var handler = new TestRig.RecordingHandler(TestRig.Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError));
        var provider = new OpenRouterJevProvider(Settings, handler);
        var ex = await Assert.ThrowsAsync<DecisionProviderException>(() => provider.Decide(TestRig.SampleRequest(), CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Equal(1, handler.Calls); // no automatic retry
    }

    [Fact]
    public async Task TimeoutIsBounded()
    {
        var handler = new TestRig.RecordingHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); });
        var provider = new OpenRouterJevProvider(Settings, handler, TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<DecisionProviderException>(() => provider.Decide(TestRig.SampleRequest(), CancellationToken.None));
        Assert.Contains("did not answer", ex.Message);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new TestRig.RecordingHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); });
        var provider = new OpenRouterJevProvider(Settings, handler, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Decide(TestRig.SampleRequest(), cts.Token));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var huge = new string('x', DecisionPolicyBounds.MaxResponseBytes + 1024);
        var handler = new TestRig.RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(huge) });
        var provider = new OpenRouterJevProvider(Settings, handler);
        await Assert.ThrowsAsync<DecisionProviderException>(() => provider.Decide(TestRig.SampleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task PayloadsWithLocalPathsAreRefused()
    {
        var handler = new TestRig.RecordingHandler(OfficialOk());
        var provider = new OpenRouterJevProvider(Settings, handler);
        var request = new DecisionRequest("m", new Dictionary<string, string> { ["intent"] = @"show C:\Users\me\secret\video.mp4" }, TestRig.SampleRequest().Questions);
        var ex = await Assert.ThrowsAsync<DecisionProviderException>(() => provider.Decide(request, CancellationToken.None));
        Assert.Contains("local path", ex.Message);
        Assert.Equal(0, handler.Calls); // refused before any network use
    }

    [Fact]
    public async Task ScrubbedRemnantsAreNotFalselyRefused()
    {
        // A scrubbed value keeps a single-backslash tail ("[local-path]\takes\x");
        // serialized JSON doubles the backslashes and the guard must not mistake
        // that encoding for a UNC path.
        var handler = new TestRig.RecordingHandler(OfficialOk());
        var provider = new OpenRouterJevProvider(Settings, handler);
        var request = new DecisionRequest("m", new Dictionary<string, string> { ["intent"] = @"show [local-path]\takes\scout.mp4" }, TestRig.SampleRequest().Questions);
        await provider.Decide(request, CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void DefaultsMatchOfficialContract()
    {
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", OpenRouterJevProvider.DefaultEndpoint);
        Assert.Equal("typesafe/jev-1.13", OpenRouterJevProvider.DefaultModel);
    }
}
