using System.Text.Json.Nodes;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

/// <summary>
/// The one discovery path for the per-user Workbench, shared by MCP and any other
/// out-of-process client, so a Unity-launched instance (always --port 0) is found
/// exactly like a standalone one. Order: explicit endpoint override → the service
/// descriptor in the canonical store (accepted only after its own /api/health
/// confirms product, protocol and instanceId) → the default loopback port.
/// </summary>
public static class WorkbenchLocator
{
    /// <summary>Environment override selecting the Workbench endpoint explicitly.</summary>
    public const string EndpointVariable = "GAMEDIRECTOR_WORKBENCH";
    public const string TokenVariable = "GAMEDIRECTOR_WORKBENCH_TOKEN";
    /// <summary>A descriptor health probe must stay cheap: discovery runs on calls
    /// that used to be instant, so a stale descriptor may cost at most this.</summary>
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);

    public static Task<WorkbenchConnection> ResolveAsync(CancellationToken ct = default) =>
        ResolveAsync(Environment.GetEnvironmentVariable(EndpointVariable), GameDirectorPaths.ServiceDescriptor(), null, ct, Environment.GetEnvironmentVariable(TokenVariable));

    /// <summary>Test seam: the override, the descriptor path and the HTTP transport
    /// are injected, so resolution is exercised without real sockets.</summary>
    internal static async Task<WorkbenchConnection> ResolveAsync(string? endpointOverride, string descriptorPath, HttpMessageHandler? handler, CancellationToken ct, string? tokenOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(endpointOverride))
        {
            if (!Uri.TryCreate(endpointOverride, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "")
                throw new ArgumentException("Workbench must be a local HTTP endpoint.");
            return new WorkbenchConnection(uri, tokenOverride);
        }
        var discovered = await TryDescriptor(descriptorPath, handler, ct);
        return discovered ?? new WorkbenchConnection(new Uri(BridgeDefaults.WorkbenchEndpoint), null);
    }

    /// <summary>Accept the descriptor only when its own health endpoint confirms the
    /// exact product, protocol and instanceId — a stale or foreign descriptor falls
    /// back to the default endpoint instead of hijacking the client.</summary>
    private static async Task<WorkbenchConnection?> TryDescriptor(string descriptorPath, HttpMessageHandler? handler, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(descriptorPath)) return null;
            var descriptor = JsonNode.Parse(File.ReadAllText(descriptorPath)) as JsonObject;
            var address = descriptor?["address"]?.GetValue<string>();
            var instanceId = descriptor?["instanceId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(instanceId)) return null;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "") return null;
            using var http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            http.Timeout = HealthProbeTimeout;
            using var response = await http.GetAsync(new Uri(uri, "/api/health"), ct);
            if (!response.IsSuccessStatusCode) return null;
            var health = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (health?["product"]?.GetValue<string>() != WorkbenchService.Product) return null;
            if (health["protocolVersion"] is not JsonValue protocol || !protocol.TryGetValue<int>(out var version) || version != WorkbenchService.ProtocolVersion) return null;
            if (health["instanceId"]?.GetValue<string>() != instanceId) return null;
            return new WorkbenchConnection(uri, descriptor?["token"]?.GetValue<string>());
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // unreadable, stale or foreign descriptor: default endpoint decides
        }
    }
}
