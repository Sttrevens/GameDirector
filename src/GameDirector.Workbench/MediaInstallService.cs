using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using GameDirector.Client;

namespace GameDirector.Workbench;

public sealed record MediaInstallStarted(string State, string OptionId, DateTimeOffset StartedUtc);
public sealed record MediaInstallRunning(string OptionId, DateTimeOffset StartedUtc, string[] OutputTail);
public sealed record MediaInstallResult(string State, string OptionId, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    MediaInstaller.Receipt? Receipt, string? Error, string[] OutputTail);
public sealed record MediaInstallSnapshot(MediaInstallRunning? Running, MediaInstallResult? LastReceipt);

// The HTTP request only admits work. One hosted worker owns the install token,
// linked to host shutdown and explicit cancellation, never RequestAborted.
// MediaInstaller retains its process/file locks and writes the full disk receipt.
public sealed class MediaInstallService : BackgroundService
{
    private const int TailLines = 40, MaxLineLength = 1000;
    private sealed class Install(MediaInstallRequest request)
    {
        public readonly MediaInstallRequest Request = request;
        public readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
        public readonly CancellationTokenSource Cancel = new();
        public readonly Queue<string> Tail = new();
    }
    private readonly object gate = new();
    private readonly Channel<Install> queue = Channel.CreateUnbounded<Install>();
    private readonly Func<MediaInstallRequest, Action<string>, CancellationToken, Task<MediaInstaller.Receipt>> install;
    private readonly Func<object> status;
    private readonly CancellationToken applicationStopping;
    private readonly ILogger<MediaInstallService> logger;
    private Install? current;
    private MediaInstallResult? lastReceipt;
    public MediaInstallService(Studio studio, IHostApplicationLifetime lifetime, ILogger<MediaInstallService> logger)
        : this((request, output, ct) => MediaToolInstaller.Install(studio, request.OptionId, ct, request.DescriptorPath, output), lifetime.ApplicationStopping, MediaToolInstaller.Status, logger) { }
    // Narrow installer seam: callers may supply another strategy executor while
    // the lifecycle/admission remains owned here. No environment mutation in tests.
    public MediaInstallService(Func<MediaInstallRequest, Action<string>, CancellationToken, Task<MediaInstaller.Receipt>> install,
        CancellationToken applicationStopping = default, Func<object>? status = null, ILogger<MediaInstallService>? logger = null)
    { this.install = install; this.applicationStopping = applicationStopping; this.status = status ?? (() => new { }); this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaInstallService>.Instance; }
    public MediaInstallStarted Start(MediaInstallRequest request)
    {
        lock (gate) {
            if (current != null) throw new StateConflictFault("A media install is already running; wait or cancel it explicitly.", retryable: true);
            if (applicationStopping.IsCancellationRequested) throw new StateConflictFault("Workbench is stopping.", retryable: true);
            if (string.IsNullOrWhiteSpace(request.OptionId)) throw new InvalidInputFault("An install optionId is required.");
            current = new Install(request); queue.Writer.TryWrite(current);
            return new("running", request.OptionId, current.StartedUtc);
        }
    }
    public MediaInstallSnapshot Snapshot()
    {
        lock (gate) return new(current == null ? null : new(current.Request.OptionId, current.StartedUtc, current.Tail.ToArray()), Copy(lastReceipt));
    }
    // Copy the receipt's arrays too: no caller can mutate future status reads.
    private static MediaInstallResult? Copy(MediaInstallResult? result) => result == null ? null : result with {
        OutputTail = result.OutputTail.ToArray(), Receipt = result.Receipt == null ? null : result.Receipt with { Output = result.Receipt.Output.ToArray() }
    };
    public JsonObject Status()
    {
        var view = Snapshot();
        var node = JsonSerializer.SerializeToNode(status(), WebJson)!.AsObject();
        node["running"] = JsonSerializer.SerializeToNode(view.Running, WebJson);
        node["lastReceipt"] = JsonSerializer.SerializeToNode(view.LastReceipt, WebJson); return node;
    }
    public MediaInstallSnapshot Cancel() { lock (gate) { current?.Cancel.Cancel(); return Snapshot(); } }
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    // Package-manager output is untrusted. Credential-bearing lines are omitted,
    // URL credentials/query/fragment are removed and each visible line is bounded.
    private static readonly Regex Secret = new(@"(^|[^a-z0-9])(authorization|bearer|password|passwd|token|secret|api[ _-]?key|access[ _-]?key|credential|key)($|[^a-z0-9])", RegexOptions.IgnoreCase);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.IgnoreCase);
    private static string Safe(string text)
    {
        if (Secret.IsMatch(text)) return "[credential-bearing output redacted]";
        text = Url.Replace(text, m => Uri.TryCreate(m.Value, UriKind.Absolute, out var uri)
            ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString() : "[URL redacted]");
        text = new string(text.Where(c => !char.IsControl(c) || c == '\t').ToArray());
        return text.Length <= MaxLineLength ? text : text[..MaxLineLength] + "…";
    }
    private void Output(Install work, string text)
    {
        lock (gate) foreach (var line in text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            work.Tail.Enqueue(Safe(line)); while (work.Tail.Count > TailLines) work.Tail.Dequeue();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken)) {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping, stoppingToken, work.Cancel.Token);
            var state = "completed"; MediaInstaller.Receipt? receipt = null; string? error = null;
            try {
                receipt = await install(work.Request, line => Output(work, line), linked.Token);
                receipt = receipt with { Output = receipt.Output.Select(Safe).TakeLast(TailLines).ToArray(), Note = receipt.Note == null ? null : Safe(receipt.Note), Error = receipt.Error == null ? null : Safe(receipt.Error) };
                if (receipt.Error != null) { state = "failed"; error = receipt.Error; }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { state = "cancelled"; error = "Installation cancelled explicitly or by Workbench shutdown."; }
            catch (Exception ex) { logger.LogError(ex, "Media installation {OptionId} failed", work.Request.OptionId); state = "failed"; error = Safe(Faults.Of(ex).Message); }
            finally {
                lock (gate) {
                    lastReceipt = new(state, work.Request.OptionId, work.StartedUtc, DateTimeOffset.UtcNow, receipt, error, work.Tail.ToArray());
                    current = null; work.Cancel.Dispose();
                }
            }
        }
    }
}

public static class MediaInstallEndpoints
{
    public static void MapMediaInstall(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/media/install", (MediaInstallService service) => service.Status());
        app.MapPost("/api/media/install", (MediaInstallRequest request, MediaInstallService service) => service.Start(request));
        app.MapDelete("/api/media/install", (MediaInstallService service) => service.Cancel());
    }
}
