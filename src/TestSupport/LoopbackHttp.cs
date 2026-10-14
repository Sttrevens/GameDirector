using System.Net;
using System.Net.Sockets;

// Linked only into test assemblies. One loopback HttpListener on an ephemeral
// 127.0.0.1 port, with the two lifecycle rules that keep it race-free:
// - Dispose is exactly one Close(), never Stop() then Close(): on the managed
//   HttpListener (macOS/Linux) Stop() already removes the prefixes and releases
//   the endpoint socket, and Close() after Stop() re-binds the port. That bind
//   can fail with EADDRINUSE (observed under load, e.g. while served
//   connections linger in TIME_WAIT, or because of another binder).
// - Start retries with a FRESH port when the bind reports the address in use.
//   The port is picked, released, and only then bound, so a parallel binder can
//   always win the gap; the retry is defensive hardening, not the fix for an
//   observed failure.
public sealed class LoopbackHttp : IDisposable
{
    private const int MaxAttempts = 5;
    public HttpListener Listener { get; }
    public int Port { get; }
    public string Endpoint => "http://127.0.0.1:" + Port; // no trailing slash
    public string Prefix => Endpoint + "/";
    private LoopbackHttp(HttpListener listener, int port) { Listener = listener; Port = port; }

    public static LoopbackHttp Start()
    {
        for (var attempt = 1; ; attempt++)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { listener.Start(); return new LoopbackHttp(listener, port); }
            catch (HttpListenerException error) when (attempt < MaxAttempts && IsAddressInUse(error)) { }
            // A failed Start() leaves the listener Closed and holding nothing,
            // so there is nothing to dispose and no state to carry into a retry.
        }
    }

    // Raw platform codes, not SocketError. Managed listener (non-Windows):
    // 48 macOS/BSD and 98 Linux (bind lost to another socket), 400 (duplicate
    // prefix registered in this process). Windows http.sys: 32 (port held by
    // another socket) and 183 (URL already registered). WSAEADDRINUSE 10048 is
    // what a raw SocketException carries; HttpListener surfaces 32/183 instead.
    private static bool IsAddressInUse(HttpListenerException error) => error.ErrorCode is 48 or 98 or 400 or 32 or 183;

    public void Dispose() => Listener.Close();
}
