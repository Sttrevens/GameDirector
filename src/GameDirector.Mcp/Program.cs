using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameDirector.Mcp;

/// <summary>
/// GameDirector MCP server (stdio). Exposes the director workflow to any
/// MCP-capable LLM host: inspect a running game's manifest, validate/compile
/// timelines offline, play/stop them in the game, and pull frames back for
/// the review loop. Heavy lifting lives in GameDirector.Core/Client; this
/// process is intentionally thin and replaceable.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // MCP stdio servers must keep stdout clean for the protocol stream.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
