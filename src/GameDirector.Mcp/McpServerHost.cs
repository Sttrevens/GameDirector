using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace GameDirector.Mcp;

/// <summary>The one server configuration — the failure filter and the tools of
/// this assembly — shared by the stdio host and the in-process contract tests,
/// so the tested surface is exactly the shipped surface.</summary>
internal static class McpServerHost
{
    public static IMcpServerBuilder Configure(IMcpServerBuilder builder) =>
        builder
            // One failure policy: tools throw; this filter turns exceptions into
            // IsError results with an actionable message instead of the SDK's
            // generic "An error occurred invoking" replacement.
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, ct) =>
            {
                try { return await next(request, ct); }
                catch (Exception ex) { return ToolErrors.ToResult(ex, ct); }
            }))
            .WithToolsFromAssembly();
}
