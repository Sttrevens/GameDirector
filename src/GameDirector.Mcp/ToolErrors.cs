using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace GameDirector.Mcp;

/// <summary>A non-2xx Workbench response: the status and the raw body travel
/// together so the error mapping can quote the server's own message.</summary>
public sealed class WorkbenchApiException : Exception
{
    public HttpStatusCode Status { get; }
    public string Body { get; }
    public WorkbenchApiException(HttpStatusCode status, string body) : base("Workbench returned HTTP " + (int)status) { Status = status; Body = body; }
}

/// <summary>The one failure policy for every MCP tool: failures surface as
/// IsError=true results whose text is actionable for the model, never a stack
/// trace. The request's own cancellation is not a tool error and propagates.</summary>
internal static class ToolErrors
{
    public static CallToolResult ToResult(Exception ex, CancellationToken requestCancellation = default)
    {
        if (ex is OperationCanceledException && requestCancellation.IsCancellationRequested)
            ExceptionDispatchInfo.Capture(ex).Throw();
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Message(ex) }] };
    }

    private static string Message(Exception ex) => ex switch
    {
        TaskCanceledException { InnerException: TimeoutException } timeout => TimedOut(timeout),
        HttpRequestException { StatusCode: null } unreachable => unreachable.Message + "; start the Workbench or the game's director mode",
        WorkbenchApiException { Status: HttpStatusCode.Unauthorized } api => "Workbench rejected the call (HTTP 401): " + ServerMessage(api.Body) + "; " + GameDirector.Client.WorkbenchConnection.UnauthorizedMessage,
        WorkbenchApiException api => "Workbench rejected the call (HTTP " + (int)api.Status + "): " + ServerMessage(api.Body),
        _ => ex.Message,
    };

    // HttpClient names its configured timeout in the cancellation message.
    private static string TimedOut(TaskCanceledException ex)
    {
        const string marker = "HttpClient.Timeout of ";
        var start = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var rest = ex.Message[(start + marker.Length)..];
            var end = rest.IndexOf(" seconds", StringComparison.Ordinal);
            if (end > 0) return "request timed out after " + rest[..end] + " s; the Workbench or game may be busy — retry";
        }
        return "request timed out; the Workbench or game may be busy — retry";
    }

    // Workbench error bodies are {"error": "..."} JSON; anything else is quoted raw.
    // Typed failures also carry code/retryable — the two facts a caller needs to
    // decide whether retrying the same request can succeed — so quote them too.
    private static string ServerMessage(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o && o["error"] is JsonValue v && v.TryGetValue<string>(out var message))
            {
                var details = new List<string>();
                if (o["code"] is JsonValue c && c.TryGetValue<string>(out var code)) details.Add("code: " + code);
                if (o["retryable"] is JsonValue r && r.TryGetValue<bool>(out var retryable)) details.Add("retryable: " + (retryable ? "true" : "false"));
                if (details.Count > 0) message += " [" + string.Join("; ", details) + "]";
                return message;
            }
        }
        catch { /* not a JSON error body: quote it as-is */ }
        return body;
    }
}
