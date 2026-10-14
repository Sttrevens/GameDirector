using System.Net;
using GameDirector.Client;
using GameDirector.Workbench;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GameDirector.Decisions.Tests;

public class BridgeFaultTests : TempPaths
{
    [Theory]
    [InlineData(400, "invalid_input", 422, false)]
    [InlineData(404, "not_found", 404, false)]
    [InlineData(403, "engine_rejected", 502, false)]
    [InlineData(500, "engine_rejected", 502, false)]
    [InlineData(503, "engine_unavailable", 503, true)]
    [InlineData(409, "state_conflict", 409, false)]
    [InlineData(504, "engine_unavailable", 503, true)]
    public async Task StudioKeepsAnAnsweringBridgesMeaning(int status, string code, int expectedStatus, bool retryable)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/manifest", async ctx => {
            ctx.Response.StatusCode = status;
            var bridgeCode = status == 400 ? "invalid_request" : status == 409 ? "state_conflict" : status == 500 ? "internal" : status == 503 ? "busy" : status == 504 ? "timeout" : "unknown";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = "fixture diagnosis", code = bridgeCode }));
        });
        await app.StartAsync();
        var root = TempPath("gd-bridge-fault-");
        try {
            using var studio = new Studio(root); studio.Register(new() { Id = "p", Name = "Fixture", Endpoint = app.Urls.Single() });
            var error = await Assert.ThrowsAnyAsync<ServiceFault>(() => studio.Connect("p", CancellationToken.None));
            var mapped = Faults.Of(error);
            Assert.Equal(code, mapped.Code); Assert.Equal(expectedStatus, mapped.Status); Assert.Equal(retryable, mapped.Retryable);
            Assert.Contains("fixture diagnosis", error.Message);
            var transport = Assert.IsType<BridgeException>(error.InnerException);
            Assert.Equal((HttpStatusCode)status, transport.StatusCode);
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
    }
}
