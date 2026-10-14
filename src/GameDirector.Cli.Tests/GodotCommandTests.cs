using GameDirector.Cli;
using Xunit;

namespace GameDirector.Cli.Tests;

public sealed class GodotCommandTests
{
    // Catches dropping the bundled Godot entrypoint or accepting misspelled options.
    [Fact]
    public async Task Godot_help_describes_manifest_and_serve_without_launching_an_engine()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await Program.Run(new[] { "godot", "--help" }, output, error));
        Assert.Contains("serve", output.ToString()); Assert.Contains("manifest", output.ToString());
        Assert.Contains("--project", output.ToString()); Assert.Empty(error.ToString());
        Assert.Contains("--port", output.ToString()); Assert.Contains("39779", output.ToString());
    }

    [Theory]
    [InlineData("oops", "--project", "x", "--stage", "res://stage.tscn")]
    [InlineData("serve", "--project", "x", "--stage", "res://stage.tscn", "--prt", "1234")]
    [InlineData("manifest", "--project", "x")]
    public async Task Godot_usage_errors_do_not_launch_an_engine(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await Program.Run(new[] { "godot" }.Concat(args).ToArray(), output, error));
        Assert.NotEmpty(error.ToString()); Assert.Empty(output.ToString());
    }
}
