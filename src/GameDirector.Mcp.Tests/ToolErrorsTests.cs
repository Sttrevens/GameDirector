using System.Net;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GameDirector.Mcp.Tests;

public class ToolErrorsTests
{
    private static string Text(CallToolResult result)
    {
        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.DoesNotContain("\n", block.Text); // single actionable line, never a stack trace
        return block.Text;
    }

    [Fact]
    public void RequestCancellationIsRethrownNotReported()
    {
        var ex = new OperationCanceledException();
        Assert.Same(ex, Assert.Throws<OperationCanceledException>(() => ToolErrors.ToResult(ex, new CancellationToken(true))));
    }

    [Fact]
    public void UnattributedCancellationIsAnOrdinaryError() =>
        Assert.Equal("Operation was canceled.", Text(ToolErrors.ToResult(new OperationCanceledException("Operation was canceled."))));

    [Fact]
    public void HttpClientTimeoutNamesTheSeconds()
    {
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.", new TimeoutException("The operation was canceled."));
        Assert.Contains("request timed out after 60 s", Text(ToolErrors.ToResult(ex)));
    }

    [Fact]
    public void UnreachableHttpNamesTheEndpointAndTheFix()
    {
        var ex = new HttpRequestException("cannot reach http://127.0.0.1:9/ (Connection refused (127.0.0.1:9))");
        var text = Text(ToolErrors.ToResult(ex));
        Assert.Contains("cannot reach http://127.0.0.1:9/", text);
        Assert.Contains("start the Workbench or the game's director mode", text);
    }

    [Fact]
    public void WorkbenchErrorsQuoteTheServerMessageAndStatus()
    {
        var text = Text(ToolErrors.ToResult(new WorkbenchApiException(HttpStatusCode.Conflict, "{\"error\":\"document changed on another client\"}")));
        Assert.Equal("Workbench rejected the call (HTTP 409): document changed on another client", text);
        Assert.Equal("Workbench rejected the call (HTTP 400): plain failure",
            Text(ToolErrors.ToResult(new WorkbenchApiException(HttpStatusCode.BadRequest, "plain failure"))));
    }

    [Fact]
    public void PlainFailuresPassTheirMessageThrough()
    {
        Assert.Equal("Supported tasks: a, b", Text(ToolErrors.ToResult(new ArgumentException("Supported tasks: a, b"))));
        var missing = Text(ToolErrors.ToResult(new IOException("Could not find file '/tmp/none.json'")));
        Assert.Contains("none.json", missing);
    }
}
