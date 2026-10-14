using System.Text.Json;
using GameDirector.Client;
using GameDirector.Workbench;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace GameDirector.Decisions.Tests;

/// <summary>The exception → (status, code, retryable) contract (fault mapping):
/// every typed fault and every mapped BCL exception lands in exactly one row,
/// and an unknown exception is a logged 500 whose body never leaks internals.</summary>
public class FaultMappingTests
{
    public static IEnumerable<object?[]> Rows()
    {
        yield return new object?[] { new EngineUnavailableFault("engine unreachable"), 503, "engine_unavailable", true };
        yield return new object?[] { new QueueFullFault("queue full"), 429, "queue_full", true };
        yield return new object?[] { new ProviderNotConfiguredFault("configure a model first"), 412, "provider_not_configured", false };
        yield return new object?[] { new InvalidInputFault("bad field"), 422, "invalid_input", false };
        yield return new object?[] { new NotFoundFault("no video"), 404, "not_found", false };
        yield return new object?[] { new StoreIntegrityFault("files preserved"), 500, "store_integrity", false };
        yield return new object?[] { new StoreIntegrityException("stored JSON damaged"), 500, "store_integrity", false };
        yield return new object?[] { new ProviderFailureFault("check endpoint and model"), 500, "provider_failure", false };
        yield return new object?[] { new EngineUnavailableException("invalid PNG payload"), 503, "engine_unavailable", true };
        yield return new object?[] { new EngineProtocolException("start receipt frameCount 61 != admission 60"), 502, "engine_rejected", false };
        yield return new object?[] { new MediaFailureException("staged ffmpeg is missing"), 500, "media_failure", false };
        yield return new object?[] { new StateConflictException("another installer owns it", retryable: true), 409, "state_conflict", true };
        yield return new object?[] { new MediaFailureFault("media process failed", new InvalidOperationException("ffmpeg stderr at /secret")), 500, "media_failure", false };
        yield return new object?[] { new RequestConflictFault("requestId reused"), 409, "request_conflict", false };
        yield return new object?[] { new StateConflictFault("prune while running", retryable: true), 409, "state_conflict", true };
        yield return new object?[] { new StateConflictFault("save first", retryable: false), 409, "state_conflict", false };
        yield return new object?[] { new DocumentConflict(new FilmDocument { Revision = 3 }), 409, "document_conflict", false };
        yield return new object?[] { new RevisionRequired(), 428, "revision_required", false };
        yield return new object?[] { new ArgumentException("bad field"), 422, "invalid_input", false };
        yield return new object?[] { new AuthoredInputException("cues[3]: unknown property"), 422, "invalid_input", false };
        yield return new object?[] { new AuthoredInputException("manifestVersion: required", AuthoredDocumentKind.Manifest), 502, "engine_rejected", false };
        yield return new object?[] { new FormatException("stored data malformed"), 500, "internal", false };
        yield return new object?[] { new NotSupportedException("unexpected runtime limitation"), 500, "internal", false };
        yield return new object?[] { new JsonException("stored JSON damaged"), 500, "internal", false };
        yield return new object?[] { new KeyNotFoundException("no such project"), 404, "not_found", false };
        yield return new object?[] { new BadHttpRequestException("Failed to read body as JSON", 400), 422, "invalid_input", false };
        yield return new object?[] { new BadHttpRequestException("body too large", 413), 413, "bad_request", false };
        yield return new object?[] { new InvalidOperationException("legacy untyped"), 500, "internal", false };
        yield return new object?[] { new IOException("store damaged at /secret/path"), 500, "internal", false };
        yield return new object?[] { new Exception("anything else"), 500, "internal", false };
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryExceptionMapsToOneRow(Exception ex, int status, string code, bool retryable)
    {
        var fault = Faults.Of(ex);
        Assert.Equal(status, fault.Status);
        Assert.Equal(code, fault.Code);
        Assert.Equal(retryable, fault.Retryable);
        var body = JsonSerializer.SerializeToNode(Faults.Body(fault))!;
        var expectedText = code == "internal" ? "Unexpected Workbench error; the detail was logged on the server."
            : ex is BadHttpRequestException ? status == 422 ? "The request body is not valid for this endpoint: " + ex.Message : "The request could not be read."
            : ex.Message;
        Assert.Equal(expectedText, body["error"]!.GetValue<string>());
        Assert.Equal(code, body["code"]!.GetValue<string>());
        Assert.Equal(retryable, body["retryable"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(400, "invalid_request", 422, "invalid_input", false)]
    [InlineData(409, "state_conflict", 409, "state_conflict", false)]
    [InlineData(503, "busy", 503, "engine_unavailable", true)]
    [InlineData(504, "timeout", 503, "engine_unavailable", true)]
    [InlineData(500, "internal", 502, "engine_rejected", false)]
    [InlineData(404, null, 404, "not_found", false)]
    [InlineData(400, null, 502, "engine_rejected", false)]
    [InlineData(400, "unknown_engine_code", 502, "engine_rejected", false)]
    [InlineData(422, null, 502, "engine_rejected", false)]
    [InlineData(403, null, 502, "engine_rejected", false)]
    public async Task BridgeProtocolDiagnosesTheCallerOnlyForDeliberateCodes(int status, string? bridgeCode, int expectedStatus, string expectedCode, bool retryable)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/manifest", async ctx => {
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "fixture diagnosis", code = bridgeCode }));
        });
        await app.StartAsync();
        try {
            using var client = new DirectorBridgeClient(app.Urls.Single());
            var error = await Assert.ThrowsAsync<BridgeException>(() => client.GetManifestAsync());
            var mapped = Faults.Of(error);
            Assert.Equal(expectedStatus, mapped.Status); Assert.Equal(expectedCode, mapped.Code); Assert.Equal(retryable, mapped.Retryable);
            Assert.Contains("fixture diagnosis", mapped.Message);
        } finally { await app.StopAsync(); }
    }

    [Fact]
    public void TypedFaultsKeepTheirMessageAndTheWireKeepsTheErrorField()
    {
        var fault = Faults.Of(new QueueFullFault("Production queue is full."));
        Assert.Equal("Production queue is full.", fault.Message);
        var body = JsonSerializer.SerializeToNode(Faults.Body(fault))!.AsObject();
        Assert.Equal("Production queue is full.", body["error"]!.GetValue<string>());
        Assert.Equal("queue_full", body["code"]!.GetValue<string>());
        Assert.True(body["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public void UnknownFaultsAnswerWithGenericTextAndNoInternals()
    {
        var fault = Faults.Of(new IOException("corrupt sector near /Users/me/secret"));
        Assert.True(fault.Unknown);
        Assert.DoesNotContain("secret", fault.Message);
        var body = JsonSerializer.Serialize(Faults.Body(fault));
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("StackTrace", body);
        Assert.DoesNotContain(" at ", body); // no stack frames
        Assert.Contains("internal", body);
    }

    [Fact]
    public void DocumentConflictKeepsTheConflictingDocument()
    {
        var fault = Faults.Of(new DocumentConflict(new FilmDocument { Revision = 7 }));
        Assert.Equal(409, fault.Status);
        var body = JsonSerializer.SerializeToNode(Faults.Body(fault), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        Assert.Equal("document_conflict", body["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.Equal(7, body["current"]!["revision"]!.GetValue<int>());
    }

    [Fact]
    public void BodyJsonErrorsAreCleanedOfSerializerBoilerplate()
    {
        var fault = Faults.Of(new BadHttpRequestException("bad body", 400, new JsonException("The JSON property 'inventedFeature' could not be mapped to any .NET member. Path: $.film | LineNumber: 0")));
        Assert.Equal(422, fault.Status);
        Assert.Contains("inventedFeature", fault.Message);
        Assert.DoesNotContain("Path:", fault.Message);
    }
}
