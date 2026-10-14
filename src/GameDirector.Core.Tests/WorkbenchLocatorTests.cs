using System.Net;
using System.Text;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>One discovery path: explicit override, then the running instance's
/// service descriptor (health-verified), then the default port. All HTTP is a
/// stub handler — no real sockets.</summary>
public class WorkbenchLocatorTests : TempPaths
{
    private static readonly string Default = BridgeDefaults.WorkbenchEndpoint;

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
    private static StubHandler Responding(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new((request, _) => Task.FromResult(respond(request)));
    private static HttpResponseMessage Health(string instanceId, int? protocol = null, string? product = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"product\":\"" + (product ?? WorkbenchService.Product) + "\",\"protocolVersion\":" + (protocol ?? WorkbenchService.ProtocolVersion) +
                ",\"instanceId\":\"" + instanceId + "\"}", Encoding.UTF8, "application/json")
        };
    private string Descriptor(string address, string instanceId)
    {
        var path = Path.Combine(TempPath("gd-descriptor-"), GameDirectorPaths.ServiceDescriptorFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"address\":\"" + address + "\",\"instanceId\":\"" + instanceId + "\",\"token\":\"owner-token\"}");
        return path;
    }
    private string Missing() => Path.Combine(TempPath("gd-no-descriptor-"), GameDirectorPaths.ServiceDescriptorFile);

    [Fact]
    public async Task EnvOverrideWinsWithoutTouchingDiskOrNetwork()
    {
        var handler = Responding(_ => throw new InvalidOperationException("no network in env mode"));
        var uri = await WorkbenchLocator.ResolveAsync("http://127.0.0.1:45678", Descriptor("http://127.0.0.1:9", "abc"), handler, CancellationToken.None);
        Assert.Equal("127.0.0.1", uri.Endpoint.Host);
        Assert.Equal(45678, uri.Endpoint.Port);
        Assert.Null(uri.Token); // endpoint override never borrows a descriptor's token
    }

    [Fact]
    public async Task ExplicitEndpointUsesOnlyItsPairedEnvironmentToken()
    {
        var resolved = await WorkbenchLocator.ResolveAsync("http://127.0.0.1:45678", Missing(), null, CancellationToken.None, "explicit-secret");
        Assert.Equal("explicit-secret", resolved.Token);
        using var client = resolved.CreateClient(TimeSpan.FromSeconds(5));
        Assert.Equal("explicit-secret", client.DefaultRequestHeaders.Authorization!.Parameter);
    }

    [Theory]
    [InlineData("http://192.168.1.10:39800")] // not loopback
    [InlineData("https://127.0.0.1:39800")]  // not http
    [InlineData("not-a-uri")]
    public async Task InvalidOverrideIsRejected(string endpoint) =>
        await Assert.ThrowsAsync<ArgumentException>(() => WorkbenchLocator.ResolveAsync(endpoint, Missing(), null, CancellationToken.None));

    [Fact]
    public async Task DescriptorWithMatchingHealthWinsOverDefault()
    {
        var handler = Responding(request =>
        {
            Assert.Equal("http://127.0.0.1:45679/api/health", request.RequestUri!.ToString());
            return Health("abc");
        });
        var uri = await WorkbenchLocator.ResolveAsync(null, Descriptor("http://127.0.0.1:45679", "abc"), handler, CancellationToken.None);
        Assert.Equal(45679, uri.Endpoint.Port);
        Assert.Equal("owner-token", uri.Token);
        using var client = uri.CreateClient(TimeSpan.FromSeconds(5));
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("owner-token", client.DefaultRequestHeaders.Authorization.Parameter);
        Assert.DoesNotContain("owner-token", uri.ToString());
    }

    [Fact]
    public async Task MismatchingInstanceIdFallsBackToDefault()
    {
        var handler = Responding(_ => Health("someone-else"));
        var uri = await WorkbenchLocator.ResolveAsync(null, Descriptor("http://127.0.0.1:45679", "abc"), handler, CancellationToken.None);
        Assert.Equal(Default, uri.Endpoint.ToString().TrimEnd('/'));
        Assert.Null(uri.Token);
    }

    [Fact]
    public async Task MismatchingProtocolFallsBackToDefault()
    {
        var handler = Responding(_ => Health("abc", protocol: WorkbenchService.ProtocolVersion + 1));
        var uri = await WorkbenchLocator.ResolveAsync(null, Descriptor("http://127.0.0.1:45679", "abc"), handler, CancellationToken.None);
        Assert.Equal(Default, uri.Endpoint.ToString().TrimEnd('/'));
        Assert.Null(uri.Token);
    }

    [Fact]
    public async Task ForeignProductFallsBackToDefault()
    {
        var handler = Responding(_ => Health("abc", product: "NotGameDirector"));
        var uri = await WorkbenchLocator.ResolveAsync(null, Descriptor("http://127.0.0.1:45679", "abc"), handler, CancellationToken.None);
        Assert.Equal(Default, uri.Endpoint.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task UnreachableDescriptorAddressFallsBackQuickly()
    {
        var handler = Responding(_ => throw new HttpRequestException("connection refused"));
        var started = DateTime.UtcNow;
        var uri = await WorkbenchLocator.ResolveAsync(null, Descriptor("http://127.0.0.1:1", "abc"), handler, CancellationToken.None);
        Assert.Equal(Default, uri.Endpoint.ToString().TrimEnd('/'));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "fallback should not wait"); // timing-under-test: a refused descriptor must fall back at once; 10 s is ~1000x the stubbed work
    }

    [Fact]
    public async Task HungDescriptorAddressIsAbandonedWithinTheProbeTimeout()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); throw new InvalidOperationException("unreachable: the probe timeout cancels first"); }); // timing-under-test: the stub must outlive the probe so the probe timeout is what ends it
        var started = DateTime.UtcNow;
        var uri = await WorkbenchLocator.ResolveAsync(null, Descriptor("http://127.0.0.1:45679", "abc"), handler, CancellationToken.None);
        Assert.Equal(Default, uri.Endpoint.ToString().TrimEnd('/'));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "health probe must be bounded by its short timeout"); // timing-under-test: the bound IS the 2 s HealthProbeTimeout; 10 s leaves 5x slack
    }

    [Fact]
    public async Task CorruptOrForeignDescriptorIsIgnored()
    {
        var corrupt = Descriptor("http://127.0.0.1:45679", "abc");
        File.WriteAllText(corrupt, "{ not json");
        var handler = Responding(_ => throw new InvalidOperationException("descriptor must not be probed"));
        Assert.Equal(Default, (await WorkbenchLocator.ResolveAsync(null, corrupt, handler, CancellationToken.None)).Endpoint.ToString().TrimEnd('/'));
        // A descriptor pointing at a non-loopback address is never followed.
        Assert.Equal(Default, (await WorkbenchLocator.ResolveAsync(null, Descriptor("http://203.0.113.9:80", "abc"), handler, CancellationToken.None)).Endpoint.ToString().TrimEnd('/'));
    }
}
