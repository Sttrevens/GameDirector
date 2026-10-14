using System.Reflection;
using System.Text;
using GameDirector.Godot;
using Xunit;

namespace GameDirector.Cli.Tests;

public sealed class GodotDockLogTests
{
    // Catches a plain StreamWriter shared by the stdout/stderr drains, and buffered logs
    // that the editor cannot read until the supervisor exits.
    [Fact]
    public async Task Concurrent_drains_preserve_every_line_and_flush_the_live_dock_log()
    {
        var drain = typeof(GodotWorkerFactory).Assembly.GetType("GameDirector.Godot.GodotProcess")!
            .GetMethod("Drain", BindingFlags.NonPublic | BindingFlags.Static)!;
        var path = Path.Combine(Path.GetTempPath(), "gd-dock-log-" + Guid.NewGuid() + ".log");
        try {
            for (var round = 0; round < 20; round++) {
                using TextWriter log = GodotDockOwner.OpenLog(path);
                var payload = new string('x', 4096);
                using var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 128).Select(i => $"out-{i}:{payload}\n")))));
                using var stderr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 128).Select(i => $"err-{i}:{payload}\n")))));
                var one = (Task)drain.Invoke(null, new object[] { stdout, log })!;
                var two = (Task)drain.Invoke(null, new object[] { stderr, log })!;
                await Task.WhenAll(one, two).WaitAsync(TestBudget.Hang);
                var lines = File.ReadAllLines(path);
                Assert.Equal(256, lines.Length);
                Assert.Equal(128, lines.Count(line => line.StartsWith("[Godot] out-")));
                Assert.Equal(128, lines.Count(line => line.StartsWith("[Godot] err-")));
            }
        } finally { File.Delete(path); }
    }
}
