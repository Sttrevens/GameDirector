using System.Text.Json;
using GameDirector.Godot;
using Xunit;

namespace GameDirector.Cli.Tests;

public sealed class GodotDockOwnerTests
{
    // Catches Windows forward/backslash or drive-letter differences invalidating a genuine proof.
    [Theory]
    [InlineData(@"c:\Games\Sandbox\", "C:/Games/Sandbox")]
    [InlineData("C:/Games/Sandbox/", "C:/Games/Sandbox")]
    [InlineData(@"c:\", "C:/")]
    [InlineData(@"\\server\share\stage\", "//server/share/stage")]
    [InlineData("/home/player/stage/", "/home/player/stage")]
    [InlineData("/", "/")]
    public void Proof_uses_canonical_project_paths(string input, string expected)
    {
        var owner = new GodotDockOwner("fixture", input);
        var reply = JsonSerializer.SerializeToElement(owner.Prove("abc"));
        Assert.Equal(expected, reply.GetProperty("project").GetString());
        Assert.Equal(GodotBridgeTests.Proof("fixture", "abc", Environment.ProcessId, expected), reply.GetProperty("proof").GetString());
    }

    // Catches public-readable owner log files, including files recreated over an existing broad mode.
    [Fact]
    public void Dock_logs_are_private_even_when_the_path_already_exists()
    {
        if (OperatingSystem.IsWindows()) return; // Unix permissions have no Windows equivalent.
        var folder = Path.Combine(Path.GetTempPath(), "gd-owner-mode-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "bridge.log");
        try {
            File.WriteAllText(path, "old");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            using var log = GodotDockOwner.OpenLog(path);
            log.WriteLine("private");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        } finally { Directory.Delete(folder, true); }
    }

    // Catches inadvertently reintroducing the per-launch secret as a command-line option.
    [Fact]
    public async Task Owner_token_is_not_accepted_on_argv()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await Program.Run(new[] { "godot", "serve", "--project", "/missing", "--stage", "res://stage.tscn", "--dock-owner", "private-token" }, output, error));
        Assert.DoesNotContain("private-token", error.ToString());
    }
}
