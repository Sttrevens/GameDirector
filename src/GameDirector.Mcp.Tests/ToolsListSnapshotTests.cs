using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Mcp.Tests;

/// <summary>
/// The MCP surface is a checked-in contract: names, required parameters and
/// readOnly/destructive/idempotent annotations. Any change fails here until a
/// human reviews the diff and re-generates ToolsList.snapshot.json.
/// </summary>
public class ToolsListSnapshotTests
{
    private const string SnapshotFile = "ToolsList.snapshot.json";

    [Fact]
    public async Task ToolsListMatchesTheReviewedSnapshot()
    {
        var tools = await McpServerFixture.ListToolsAsync();
        var actual = Canonical(tools);
        if (Environment.GetEnvironmentVariable("GD_UPDATE_SNAPSHOT") == "1")
        {
            // Deliberate surface change: regenerate the checked-in contract, then review the git diff.
            var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            await File.WriteAllTextAsync(Path.Combine(projectDir, SnapshotFile), actual);
            return;
        }
        var path = Path.Combine(AppContext.BaseDirectory, SnapshotFile);
        Assert.True(File.Exists(path), SnapshotFile + " is missing next to the test assembly");
        var expected = await File.ReadAllTextAsync(path);
        if (actual != expected)
            Assert.Fail("The MCP tools/list surface changed. Review the new surface below; if the change is intended, " +
                "regenerate src/GameDirector.Mcp.Tests/" + SnapshotFile + " (GD_UPDATE_SNAPSHOT=1) and review the diff.\n" + actual);
    }

    // Stable shape: tools sorted by name; per tool the required parameters in
    // declaration order and the annotations the host uses to gate change.
    internal static string Canonical(IReadOnlyList<ModelContextProtocol.Client.McpClientTool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var required = new JsonArray();
            if (tool.ProtocolTool.InputSchema.TryGetProperty("required", out var names))
                foreach (var name in names.EnumerateArray()) required.Add(name.GetString());
            var annotations = new JsonObject();
            if (tool.ProtocolTool.Annotations?.ReadOnlyHint == true) annotations["readOnly"] = true;
            if (tool.ProtocolTool.Annotations?.DestructiveHint == true) annotations["destructive"] = true;
            if (tool.ProtocolTool.Annotations?.IdempotentHint == true) annotations["idempotent"] = true;
            var row = new JsonObject
            {
                ["name"] = tool.Name,
                ["required"] = required,
                ["annotations"] = annotations,
            };
            // Validation's optional capture geometry is a reviewed surface too.
            if (tool.Name == McpToolNames.ValidateTimeline) {
                var optional = new JsonArray();
                foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
                    if (!required.Any(n => n!.GetValue<string>() == property.Name)) optional.Add(property.Name);
                row["optional"] = optional;
            }
            array.Add(row);
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
