using System.Reflection;
using System.Text.RegularExpressions;
using GameDirector.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GameDirector.Mcp.Tests;

/// <summary>
/// The grammar an LLM reads and the tools a host lists are one contract:
/// the grammar may only reference tools that exist, every tool is pinned by a
/// name constant, and every parameter is described.
/// </summary>
public class GrammarSurfaceTests
{
    [Fact]
    public async Task GrammarReferencesOnlyExistingTools()
    {
        var names = (await McpServerFixture.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        var referenced = Regex.Matches(DirectorProduct.Grammar(), @"gd_[a-z_]+").Select(m => m.Value).Distinct();
        foreach (var name in referenced)
            Assert.True(names.Contains(name), "the grammar references " + name + ", which is not an MCP tool");
    }

    [Fact]
    public async Task EveryListedToolIsPinnedByANameConstant()
    {
        var listed = (await McpServerFixture.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);
        var pinned = typeof(McpToolNames).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetValue(null)!).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(pinned, listed);
    }

    [Fact]
    public async Task EveryParameterHasADescription()
    {
        foreach (var tool in await McpServerFixture.ListToolsAsync())
        {
            Assert.True(tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties), tool.Name + " has no properties object");
            foreach (var property in properties.EnumerateObject())
            {
                Assert.False(property.Name.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase), tool.Name + " leaks its CancellationToken");
                Assert.True(property.Value.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
                    tool.Name + "." + property.Name + " has no [Description]");
            }
        }
    }

    [Fact] public async Task AudioImportDescriptionsMatchTheExecutableLimitsAndFormats()
    {
        var tool = (await McpServerFixture.ListToolsAsync()).Single(t => t.Name == McpToolNames.StudioMediaImport);
        var parameter = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("filePath").GetProperty("description").GetString()!;
        foreach (var text in new[] { tool.ProtocolTool.Description!, parameter }) {
            var size = Regex.Match(text, @"up to (\d+) MiB"); Assert.True(size.Success);
            Assert.Equal(GameDirector.Core.Dsl.FilmLimits.MaxAudioImportBytes, int.Parse(size.Groups[1].Value) * 1024 * 1024);
            var formats = Regex.Match(text, @"[A-Z0-9]+(?:/[A-Z0-9]+)+"); Assert.True(formats.Success);
            Assert.Equal(MediaFormats.AudioExtensions.Select(e => e.TrimStart('.').ToUpperInvariant()), formats.Value.Split('/'));
        }
    }

    [Fact]
    public async Task McpGrammarPrintsTheGeneratedContractVerbatim()
    {
        var result = await McpServerFixture.WithClientAsync(client =>
            client.CallToolAsync(McpToolNames.Grammar, new Dictionary<string, object?>()).AsTask());
        Assert.True(result.IsError != true);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(DirectorProduct.Grammar(), text);
    }
}
