using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Xunit;

namespace GameDirector.Core.Tests;

// LoopbackHttp and the managed-HttpListener re-bind it exists to avoid.
public class LoopbackHttpTests
{
    // Close() after Stop() re-runs RemoveListener; the endpoint is gone after
    // Stop(), so GetEPListener creates and BINDS a fresh endpoint listener for
    // the port and loses to any live binder. Deterministic: the binder is us.
    [Fact]
    public void CloseAfterStopRebindsThePortAndLosesToALiveBinder()
    {
        if (OperatingSystem.IsWindows()) return; // http.sys owns prefixes; the managed re-bind path does not exist there.
        var server = LoopbackHttp.Start(); // helper-held port: no pick-then-bind gap of its own
        try
        {
            server.Listener.Stop(); // HttpListenerStop: deliberate — drives the re-bind this test documents.
            using var holder = new TcpListener(IPAddress.Loopback, server.Port);
            holder.Start(); // never serves: no TIME_WAIT, so the re-bind can only lose to this live holder
            var error = Assert.Throws<HttpListenerException>(() => server.Listener.Close());
            Assert.Contains("Address already in use", error.Message);
        }
        finally { try { server.Dispose(); } catch (HttpListenerException) { } } // listener already Stop()+Close()d above; best-effort only
    }

    // What LoopbackHttp.Dispose actually guarantees, observable from outside:
    // the port is held while the helper lives and released after Dispose. A
    // Stop()+Close() regression cannot be seen black-box (any second binder is
    // hidden behind the re-bind), so NoHttpListenerStopInTestSources guards it.
    [Fact]
    public void HelperHoldsThePortWhileAliveAndReleasesItOnDispose()
    {
        if (OperatingSystem.IsWindows()) return; // managed-listener claims; http.sys holds the port kernel-side, a user-mode bind is not a probe.
        int port;
        using (var server = LoopbackHttp.Start())
        {
            port = server.Port;
            using var attempt = new TcpListener(IPAddress.Loopback, port); // disposed even if the assert fails
            var taken = Assert.Throws<SocketException>(() => attempt.Start());
            Assert.Equal(SocketError.AddressAlreadyInUse, taken.SocketErrorCode);
        }
        // Released by the single Close(): a fresh bind succeeds. Bounded retries
        // exist only so a momentary unrelated holder cannot fail this test for
        // the wrong reason; a helper that leaked the port fails all of them.
        for (var attempt = 1; ; attempt++)
        {
            try { using var freed = new TcpListener(IPAddress.Loopback, port); freed.Start(); return; }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 25) { Thread.Sleep(40); }
        }
    }

    // Source guard: no HttpListener ever receives .Stop() in test sources, so
    // the Stop()+Close() re-bind cannot come back through any helper, fixture
    // or test. Narrow by construction: a call is flagged only when the receiver
    // is declared as HttpListener in the same file or the chain ends in
    // .Listener — TimelinePlayer.Stop(), p.Stop() on other types and the
    // TcpListener.Stop() in UnusedEndpoint() are never matched. A deliberate
    // call opts out with "HttpListenerStop: deliberate" on the same line.
    [Fact]
    public void NoHttpListenerStopInTestSources()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var src = Path.Combine(root, "src");
        Assert.True(Directory.Exists(Path.Combine(src, "TestSupport")), "repo layout changed: src/TestSupport not found");
        var files = Directory.GetFiles(Path.Combine(src, "TestSupport"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetDirectories(src, "*Tests").SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories)))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file); var text = string.Join("\n", lines);
            var typed = Regex.Matches(text, @"\bHttpListener\s+(\w+)").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(text, @"\bvar\s+(\w+)\s*=\s*new\s+HttpListener\b").Select(m => m.Groups[1].Value))
                .ToHashSet();
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("HttpListenerStop: deliberate")) continue;
                foreach (Match call in Regex.Matches(lines[i], @"(\w+(?:\.\w+)*)\.Stop\s*\("))
                {
                    var receiver = call.Groups[1].Value;
                    if (typed.Contains(receiver) || receiver.Split('.').Last() == "Listener")
                        offenders.Add(file.Substring(root.Length) + ":" + (i + 1) + "  " + lines[i].Trim());
                }
            }
        }
        Assert.Empty(offenders);
    }
}
