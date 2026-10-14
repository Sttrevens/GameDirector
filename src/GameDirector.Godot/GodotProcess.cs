using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

// One child/connection per take. Disposal always kills and observes the owned child.
internal sealed class GodotProcess : IGodotWorker
{
    private readonly Process process;
    private readonly Task stdout, stderr;
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    public JsonElement Facts { get; }
    private GodotProcess(Process process, Task stdout, Task stderr, TcpClient client, JsonElement facts)
    { this.process = process; this.stdout = stdout; this.stderr = stderr; this.client = client; stream = client.GetStream(); Facts = facts; }
    internal static async Task<GodotProcess> Open(string binary, string project, string stage, bool headless, TextWriter log, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = project };
        foreach (var arg in new[] { "--path", project, "--disable-render-loop" }) info.ArgumentList.Add(arg);
        if (headless) info.ArgumentList.Add("--headless");
        else if (OperatingSystem.IsMacOS()) { info.ArgumentList.Add("--rendering-method"); info.ArgumentList.Add("forward_plus"); info.ArgumentList.Add("--rendering-driver"); info.ArgumentList.Add("metal"); }
        foreach (var arg in new[] { "res://addons/gamedirector/worker.tscn", "--", "--gd-port", port.ToString(), "--gd-token", token, "--gd-host", BridgeDefaults.LoopbackHost, "--gd-max-bytes", BridgeLimits.MaxRequestBytes.ToString() }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("could not launch Godot");
        log = TextWriter.Synchronized(log);
        var stdout = Drain(process.StandardOutput, log); var stderr = Drain(process.StandardError, log); TcpClient? client = null;
        try {
            client = await listener.AcceptTcpClientAsync(ct); client.NoDelay = true;
            var hello = await Read(client.GetStream(), ct);
            if (hello.GetProperty("token").GetString() != token) throw new InvalidOperationException("Godot worker authentication failed");
            await Write(client.GetStream(), new { token, contract = EngineContract.Current, bindings = GodotContract.Bindings, stage }, ct);
            var reply = await Read(client.GetStream(), ct); Check(reply);
            return new(process, stdout, stderr, client, reply.GetProperty("facts").Clone());
        } catch { client?.Dispose(); await Kill(process, stdout, stderr); throw; }
    }
    public async Task<JsonElement> Inspect(CancellationToken ct) => (await Command(new { op = "inspect" }, ct)).GetProperty("manifest").Clone();
    public async Task Begin(CompiledTimeline timeline, int width, int height, CancellationToken ct) =>
        _ = await Command(new { op = "begin", compiled = timeline, width, height }, ct);
    public async Task<WorkerFrame> Frame(int index, int fps, bool advanceOnly, CancellationToken ct, bool measure = false)
    {
        var reply = await Command(new { op = "frame", index, fps, advanceOnly, measure, nextTime = FrameClock.TimeOf(index + 1, fps) }, ct);
        var n = reply.GetProperty("pngBytes").GetInt32();
        var maximum = checked(CaptureMaximum());
        if (n < 0 || n > maximum || advanceOnly && n != 0) throw new InvalidOperationException("invalid worker PNG length");
        byte[]? png = n == 0 ? null : new byte[n]; if (png != null) await stream.ReadExactlyAsync(png, ct);
        return new(png, reply.TryGetProperty("review", out var review) ? review.Clone() : null, reply.TryGetProperty("pose", out var pose) ? pose.Clone() : null, reply.TryGetProperty("measure", out var row) ? row.Clone() : null);
    }
    private static int CaptureMaximum() => checked(EngineContract.Current.Capture.MaxWidth * EngineContract.Current.Capture.MaxHeight * 4 + BridgeLimits.MaxRequestBytes);
    public async Task<JsonElement> Finish(CancellationToken ct) => (await Command(new { op = "finish" }, ct)).GetProperty("events").Clone();
    private async Task<JsonElement> Command(object value, CancellationToken ct) { await Write(stream, value, ct); var r = await Read(stream, ct); Check(r); return r; }
    private static void Check(JsonElement r)
    {
        if (!r.TryGetProperty("error", out var error)) return;
        var code = r.GetProperty("code").GetString(); var definition = BridgeErrors.All.SingleOrDefault(e => e.Code == code) ?? BridgeErrors.Internal;
        throw new BridgeRequestException(definition, error.GetString() ?? "worker failure");
    }
    private static async Task Write(NetworkStream stream, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(DslJson.Serialize(value)); var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(bytes, ct);
    }
    private static async Task<JsonElement> Read(NetworkStream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct); var n = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (n <= 0 || n > BridgeLimits.MaxRequestBytes) throw new InvalidOperationException("invalid worker JSON length");
        var bytes = new byte[n]; await stream.ReadExactlyAsync(bytes, ct); using var doc = JsonDocument.Parse(bytes); return doc.RootElement.Clone();
    }
    private static async Task Drain(StreamReader reader, TextWriter log) { string? line; while ((line = await reader.ReadLineAsync()) != null) { await log.WriteLineAsync("[Godot] " + line); await log.FlushAsync(); } }
    private static async Task Kill(Process process, Task stdout, Task stderr)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
        finally { process.Dispose(); }
    }
    public async ValueTask DisposeAsync() { client.Dispose(); await Kill(process, stdout, stderr); }
}
