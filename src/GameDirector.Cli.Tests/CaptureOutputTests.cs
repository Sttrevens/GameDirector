using System.Net;
using System.Text;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Cli.Tests;

public class CaptureOutputTests : TempPaths
{
    [Fact]
    public async Task CaptureWritesBytesOutsideSourceAndRefusesProtectedPathsBeforeFetchingPixels()
    {
        var root = TempPath("gd-capture-output-");
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, ".git"));
        var server = LoopbackHttp.Start();
        var endpoint = server.Endpoint; var listener = server.Listener;
        var pixels = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }; var captures = 0;
        var serve = Task.Run(async () => {
            try { while (true) {
                var ctx = await listener.GetContextAsync();
                var png = ctx.Request.Url!.AbsolutePath == "/capture";
                if (png) captures++;
                var data = png ? pixels : Encoding.UTF8.GetBytes(DslJson.Serialize(new { capabilities = new Dictionary<string, string> { ["project.sourceRoot"] = source } }));
                ctx.Response.ContentType = png ? "image/png" : "application/json"; ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data); ctx.Response.Close();
            }} catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        });
        try {
            var output = Path.Combine(root, "external", "frame.png");
            var stdout = new StringWriter(); var stderr = new StringWriter();
            Assert.Equal(0, await Program.Run(new[] { "capture", "--endpoint", endpoint, "--out", output }, stdout, stderr));
            Assert.Equal(pixels, File.ReadAllBytes(output)); Assert.Equal(1, captures); Assert.Empty(stderr.ToString());
            foreach (var path in new[] { Path.Combine(source, "Assets", "frame.png"), Path.Combine(source, "frame.png") }) {
                stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
                Assert.Equal(1, await Program.Run(new[] { "capture", "--endpoint", endpoint, "--out", path }, stdout, stderr));
                Assert.False(File.Exists(path)); Assert.Empty(stdout.ToString()); Assert.NotEmpty(stderr.ToString()); Assert.Equal(1, captures);
            }
        }
        finally { server.Dispose(); await serve; Directory.Delete(root, true); }
    }
}
