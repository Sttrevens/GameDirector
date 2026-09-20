using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

public class PinnedDownloadTests : IDisposable
{
    private readonly HttpListener listener;
    private readonly string prefix;
    private byte[] payload = Encoding.UTF8.GetBytes("pinned ffmpeg fixture");
    private int redirects;
    public PinnedDownloadTests()
    {
        prefix = "http://127.0.0.1:" + FreePort() + "/";
        listener = new HttpListener(); listener.Prefixes.Add(prefix); listener.Start();
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
    private static int FreePort() { using var s = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); s.Start(); return ((IPEndPoint)s.LocalEndpoint).Port; }
    public void Dispose() { try { listener.Stop(); } catch { } }
    private string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact] public async Task HappyPathWritesVerifiedBytes()
    {
        var target = Path.Combine(Path.GetTempPath(), "gd-dl-" + Guid.NewGuid().ToString("N"));
        await PinnedDownload.Fetch(new Uri(prefix), Hash(payload), 1 << 20, target);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        File.Delete(target);
    }

    [Fact] public async Task HashMismatchRefusesAndLeavesNothing()
    {
        var target = Path.Combine(Path.GetTempPath(), "gd-dl-" + Guid.NewGuid().ToString("N"));
        var wrong = new string('0', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PinnedDownload.Fetch(new Uri(prefix), wrong, 1 << 20, target));
        Assert.False(File.Exists(target));
    }

    [Fact] public async Task UnpinnedAndPlainRemoteHttpAreRefused()
    {
        var target = Path.Combine(Path.GetTempPath(), "gd-dl-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(() => PinnedDownload.Fetch(new Uri(prefix), "", 1 << 20, target));
        await Assert.ThrowsAsync<ArgumentException>(() => PinnedDownload.Fetch(new Uri("http://example.com/x"), Hash(payload), 1 << 20, target));
    }

    [Fact] public async Task SizeBudgetIsEnforced()
    {
        var target = Path.Combine(Path.GetTempPath(), "gd-dl-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PinnedDownload.Fetch(new Uri(prefix), Hash(payload), 4, target));
        Assert.False(File.Exists(target));
    }

    [Fact] public async Task RedirectStormRefuses()
    {
        redirects = 10;
        var target = Path.Combine(Path.GetTempPath(), "gd-dl-" + Guid.NewGuid().ToString("N"));
        // Loopback-to-loopback redirects never cross hosts; only the hop budget stops a storm.
        await Assert.ThrowsAsync<InvalidOperationException>(() => PinnedDownload.Fetch(new Uri(prefix), Hash(payload), 1 << 20, target));
    }
}
