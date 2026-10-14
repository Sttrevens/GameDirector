using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GameDirector.Mcp.Tests;

/// <summary>The real server configuration, hosted in-process over a stream
/// pair and driven through the SDK client — the tested surface is the shipped
/// surface, no reflection shortcut that could diverge from it.</summary>
internal static class McpServerFixture
{
    public static async Task<T> WithClientAsync<T>(Func<McpClient, Task<T>> run)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddFilter(_ => false); // tests assert on tool data, not logs
        McpServerHost.Configure(builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()));
        var host = builder.Build();
        await host.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
            return await run(client);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
            host.Dispose();
        }
    }

    public static Task<IReadOnlyList<McpClientTool>> ListToolsAsync() =>
        WithClientAsync(async client => (IReadOnlyList<McpClientTool>)await client.ListToolsAsync());
}
