using GameDirector.Core.Capture;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

/// <summary>A bridge call failed in a way the caller can act on. Kind classifies
/// it; Message is the final wording, shared verbatim by CLI and MCP so neither
/// formats its own version of the same failure.</summary>
public sealed class BridgeException : Exception
{
    public enum FailureKind { Unreachable, Rejected, Busy, Timeout }
    public FailureKind Kind { get; }
    public string Endpoint { get; }
    public System.Net.HttpStatusCode? StatusCode { get; }
    public string? Body { get; }
    public string? Code { get; }

    private BridgeException(FailureKind kind, string endpoint, string message, System.Net.HttpStatusCode? status = null, string? body = null, Exception? inner = null, string? code = null)
        : base(message, inner) { Kind = kind; Endpoint = endpoint; StatusCode = status; Body = body; Code = code; }

    // Only the transport produces these facts. Callers never infer reachability
    // from exception types or lose the answering engine's status/body.
    internal static BridgeException Unreachable(string endpoint, HttpRequestException ex) =>
        new(FailureKind.Unreachable, endpoint, $"bridge unreachable at {endpoint} (is the game running in director mode?)", inner: ex);
    internal static BridgeException Answered(string endpoint, System.Net.HttpStatusCode status, string body, string? detail = null, Exception? inner = null)
    {
        var kind = status == System.Net.HttpStatusCode.RequestTimeout || (int)status == BridgeErrors.Timeout.Status ? FailureKind.Timeout
            : status == System.Net.HttpStatusCode.TooManyRequests || (int)status == BridgeErrors.Busy.Status ? FailureKind.Busy : FailureKind.Rejected;
        string? code = null;
        try {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            if(json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && json.RootElement.TryGetProperty("code",out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)code=value.GetString();
        } catch(System.Text.Json.JsonException) { } // keep even a non-JSON answer intact
        var reason = kind == FailureKind.Busy ? "is busy" : kind == FailureKind.Timeout ? "timed out" : "rejected the request";
        return new(kind, endpoint, $"bridge at {endpoint} {reason} (HTTP {(int)status})" + (detail == null ? "" : "; " + detail) + ": " + body, status, body, inner, code);
    }
    internal static BridgeException TimedOut(string endpoint, int timeoutSeconds, OperationCanceledException ex, System.Net.HttpStatusCode? status = null) =>
        new(FailureKind.Timeout, endpoint,
            $"bridge at {endpoint} did not answer within {timeoutSeconds}s (game busy, or the editor is paused / at a breakpoint); retry", status, inner: ex);
}

/// <summary>
/// The one implementation of the director operations every surface shares.
/// CLI and MCP are formatters over these calls: validate/compile load the
/// authored timeline and the manifest (file or live bridge) and return the
/// typed Core verdict; bridge calls go through the single reachability wrapper.
/// </summary>
public static class DirectorOperations
{
    /// <summary>An authored timeline plus the manifest it is judged against.</summary>
    public sealed record Input(TimelineAsset Timeline, CapabilityManifest Manifest, string TimelinePath, string? ManifestPath);

    /// <summary>Strict-authored timeline; manifest from file, or from the live
    /// game bridge when no manifest path is given. Each document is loaded on its
    /// own so a parse refusal names its document structurally: only the caller's
    /// timeline is an authored verdict, an engine manifest is skew/fault.</summary>
    public static async Task<Input> LoadInput(string timelinePath, string? manifestPath, string? endpoint, CancellationToken ct = default)
    {
        TimelineAsset timeline;
        try { timeline = DslJson.LoadAuthored<TimelineAsset>(timelinePath); }
        catch (AuthoredInputException ex) { throw new AuthoredInputException(ex.Message, AuthoredDocumentKind.Timeline); }
        CapabilityManifest manifest;
        try {
            manifest = manifestPath != null
                ? DslJson.LoadAuthored<CapabilityManifest>(manifestPath)
                : await WithReachableBridge(endpoint, c => c.GetManifestAsync(ct), ct);
        }
        catch (AuthoredInputException ex) { throw new AuthoredInputException(ex.Message, AuthoredDocumentKind.Manifest); }
        RequireValidManifest(manifest);
        return new Input(timeline, manifest, timelinePath, manifestPath);
    }

    internal static void RequireValidManifest(CapabilityManifest manifest)
    {
        var verdict = TimelineCompiler.ValidateManifest(manifest);
        if (verdict.HasErrors)
            throw new EngineProtocolException("invalid engine manifest: " + string.Join("; ", verdict.Diagnostics));
    }

    /// <summary>The falsifier: compile an authored timeline against a manifest.</summary>
    public static async Task<CompileResult> ValidateTimeline(string timelinePath, string? manifestPath, string? endpoint, CancellationToken ct = default, int? width = null, int? height = null)
    {
        var input = await LoadInput(timelinePath, manifestPath, endpoint, ct);
        return TimelineCompiler.Compile(input.Timeline, input.Manifest, width > 0 && height > 0 ? (double?)width / height : null);
    }

    /// <summary>The take gate an engine would apply, computed client-side:
    /// compiled timeline, frame count and admission verdict in one result.</summary>
    public static async Task<AdmissionResult> AdmitTake(string timelinePath, string? manifestPath, string? endpoint, int fps, int width, int height, CancellationToken ct = default, int captureFromFrame = 0, bool explicitSize = true)
    {
        var input = await LoadInput(timelinePath, manifestPath, endpoint, ct);
        return TakeAdmission.Admit(input.Timeline, input.Manifest, fps, width, height, captureFromFrame, explicitSize);
    }

    /// <summary>Run one bridge operation with a disposed client and classified
    /// failures. The caller's own cancellation is never reclassified.</summary>
    public static async Task WithReachableBridge(string? endpoint, Func<DirectorBridgeClient, Task> operation, CancellationToken ct = default) =>
        await WithReachableBridge<object?>(endpoint, async c => { await operation(c); return null; }, ct);

    /// <summary>Run one bridge operation with a disposed client and classified
    /// failures. The caller's own cancellation is never reclassified.</summary>
    public static async Task<T> WithReachableBridge<T>(string? endpoint, Func<DirectorBridgeClient, Task<T>> operation, CancellationToken ct = default)
    {
        using var client = new DirectorBridgeClient(endpoint);
        return await operation(client);
    }
}
