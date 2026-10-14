using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

public class PinnedDownloadTests : TempPaths
{
    private readonly LoopbackHttp server;
    private readonly HttpListener listener;
    private readonly string prefix;
    private byte[] payload = Encoding.UTF8.GetBytes("pinned ffmpeg fixture");
    private int redirects;
    public PinnedDownloadTests()
    {
        server = LoopbackHttp.Start(); listener = server.Listener; prefix = server.Prefix;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                if (redirects > 0) { redirects--; ctx.Response.Redirect(prefix + "final"); ctx.Response.StatusCode = 302; }
                else { ctx.Response.StatusCode = 200; ctx.Response.ContentType = "application/octet-stream"; await ctx.Response.OutputStream.WriteAsync(payload); }
                try { ctx.Response.Close(); } catch { }
            }
        });
    }
    // One Close() via the helper; Stop() first would make Close() re-bind the port.
    public override void Dispose() { try { server.Dispose(); } finally { base.Dispose(); } }
    private string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact] public async Task HappyPathWritesVerifiedBytes()
    {
        var target = TempPath("gd-dl-");
        await PinnedDownload.Fetch(new Uri(prefix), Hash(payload), 1 << 20, target);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        File.Delete(target);
    }

    [Fact] public async Task HashMismatchRefusesAndLeavesNothing()
    {
        var target = TempPath("gd-dl-");
        var wrong = new string('0', 64);
        var error = await Assert.ThrowsAsync<MediaFailureException>(() => PinnedDownload.Fetch(new Uri(prefix), wrong, 1 << 20, target));
        Assert.Equal("Downloaded bytes differ from the pinned SHA-256; the artifact was not installed.", error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact] public async Task UnpinnedAndPlainRemoteHttpAreRefused()
    {
        var target = TempPath("gd-dl-");
        await Assert.ThrowsAsync<ArgumentException>(() => PinnedDownload.Fetch(new Uri(prefix), "", 1 << 20, target));
        await Assert.ThrowsAsync<ArgumentException>(() => PinnedDownload.Fetch(new Uri("http://example.com/x"), Hash(payload), 1 << 20, target));
    }

    [Fact] public async Task SizeBudgetIsEnforced()
    {
        var target = TempPath("gd-dl-");
        var error = await Assert.ThrowsAsync<MediaFailureException>(() => PinnedDownload.Fetch(new Uri(prefix), Hash(payload), 4, target));
        Assert.Equal("Download exceeds the pinned size budget.", error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact] public async Task RedirectStormRefuses()
    {
        redirects = 10;
        var target = TempPath("gd-dl-");
        // Loopback-to-loopback redirects never cross hosts; only the hop budget stops a storm.
        var error = await Assert.ThrowsAsync<MediaFailureException>(() => PinnedDownload.Fetch(new Uri(prefix), Hash(payload), 1 << 20, target));
        Assert.Equal("Redirect policy refused: hops are bounded and must stay on HTTPS.", error.Message);
    }
}
