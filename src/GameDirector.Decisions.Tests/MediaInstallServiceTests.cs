using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Workbench;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameDirector.Decisions.Tests;

// A fake process is the only seam: the singleton worker, cancellation, snapshots
// and endpoint mappings remain real. No package manager is executed here.
public class MediaInstallServiceTests
{
    [Fact]
    public async Task StartReturnsImmediatelyAndDuplicateStartIsAStateConflict()
    {
        var process = new FakeInstall(); using var service = new MediaInstallService(process.Run);
        // Production hands ExecuteAsync to the host's thread pool. Task.Run does the
        // same here: SynchronizationContext.Current is null inside it, so the worker's
        // `await foreach` never queues behind every other test class on xUnit's shared
        // MaxConcurrencySyncContext — which is what made a trivial handshake exceed 3 s.
        await Task.Run(() => service.StartAsync(CancellationToken.None));
        var started = service.Start(new("fixture"));
        // "Start returns immediately" causally, not by stopwatch: nothing has set
        // process.Finish, so the install cannot have completed. Start returning a
        // running receipt with no last receipt is the proof it never waited for it.
        Assert.False(process.Finish.Task.IsCompleted);
        Assert.Equal("running", started.State); Assert.Equal("fixture", started.OptionId);
        Assert.NotNull(service.Snapshot().Running); Assert.Null(service.Snapshot().LastReceipt);
        await process.Entered.Task.WaitAsync(TestBudget.Hang);
        var fault = Assert.Throws<StateConflictFault>(() => service.Start(new("second")));
        Assert.Equal(409, fault.Status); Assert.Equal("state_conflict", fault.Code);
        Assert.Equal(started.StartedUtc, service.Snapshot().Running!.StartedUtc);
        process.Finish.SetResult(); await Finished(service);
        Assert.Null(service.Snapshot().Running);
        Assert.Equal("completed", service.Snapshot().LastReceipt!.State);
        Assert.True(service.Snapshot().LastReceipt!.Receipt!.Installed);
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyExplicitCancelOrHostShutdownStopsTheProcess(bool shutdown)
    {
        using var lifetime = new CancellationTokenSource(); var process = new FakeInstall();
        using var service = new MediaInstallService(process.Run, lifetime.Token);
        await Task.Run(() => service.StartAsync(CancellationToken.None)); service.Start(new("fixture"));
        await process.Entered.Task.WaitAsync(TestBudget.Hang);
        if (shutdown) lifetime.Cancel(); else service.Cancel();
        await Finished(service);
        Assert.Equal("cancelled", service.Snapshot().LastReceipt!.State);
        Assert.Null(service.Snapshot().Running);
        await process.Stopped.Task.WaitAsync(TestBudget.Hang);
        // Cancellation is idempotent and a new explicit start gets a new token.
        service.Cancel();
        if (!shutdown) {
            process.Finish.TrySetResult(); service.Start(new("again")); await Finished(service);
            Assert.Equal("completed", service.Snapshot().LastReceipt!.State);
        }
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TailIsLiveBoundedAndRedactedAndFailuresHaveAFinalReceipt()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new MediaInstallService(async (_, log, _) => {
            for (var i = 0; i < 70; i++) log("line " + i);
            log("Authorization: Bearer do-not-leak");
            log("download https://name:password@local.invalid/file?token=do-not-leak");
            entered.SetResult(); await release.Task; throw new MediaFailureException("token=do-not-leak");
        });
        await Task.Run(() => service.StartAsync(CancellationToken.None)); service.Start(new("fixture"));
        await entered.Task.WaitAsync(TestBudget.Hang);
        var tail = service.Snapshot().Running!.OutputTail;
        Assert.InRange(tail.Length, 38, 40); Assert.Contains("line 69", tail); Assert.DoesNotContain("line 0", tail);
        Assert.DoesNotContain("do-not-leak", string.Join("\n", tail));
        Assert.DoesNotContain("password", string.Join("\n", tail));
        tail[0] = "caller cannot change stored status";
        Assert.DoesNotContain(tail[0], service.Snapshot().Running!.OutputTail);
        release.SetResult(); await Finished(service);
        Assert.Equal("failed", service.Snapshot().LastReceipt!.State);
        Assert.DoesNotContain("do-not-leak", service.Status().ToJsonString());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HttpStartConflictCancelAndDisconnectedRequestUseTheSameWorker()
    {
        var process = new FakeInstall(); using var service = new MediaInstallService(process.Run);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service); builder.Services.AddHostedService(s => s.GetRequiredService<MediaInstallService>());
        await using var app = builder.Build();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var posts = 0;
        app.Use(async (ctx, next) => {
            var delayResponse = ctx.Request.Method == "POST" && Interlocked.Increment(ref posts) == 1;
            try {
                await next();
                // Hold only the first response open so this test forces a real
                // RequestAborted after the production endpoint starts its work.
                if (delayResponse) await Task.Delay(Timeout.Infinite, ctx.RequestAborted); // timing-under-test: the hold IS the client disconnect under test
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { aborted.TrySetResult(); }
            catch (ServiceFault fault) { ctx.Response.StatusCode = fault.Status; await ctx.Response.WriteAsJsonAsync(Faults.Body(Faults.Of(fault))); }
        });
        app.MapMediaInstall(); await Task.Run(() => app.StartAsync()); // the hosted worker starts off the test sync context, as it does in production
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var disconnected = new CancellationTokenSource();
        var started = http.PostAsJsonAsync("/api/media/install", new MediaInstallRequest("fixture"), disconnected.Token);
        await process.Entered.Task.WaitAsync(TestBudget.Hang); disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await started; });
        await aborted.Task.WaitAsync(TestBudget.Hang);
        var status = JsonNode.Parse(await http.GetStringAsync("/api/media/install"))!;
        Assert.Equal("fixture", status["running"]!["optionId"]!.GetValue<string>());
        using var duplicate = await http.PostAsJsonAsync("/api/media/install", new MediaInstallRequest("fixture"));
        Assert.Equal(409, (int)duplicate.StatusCode);
        Assert.Equal("state_conflict", JsonNode.Parse(await duplicate.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        using var cancel = await http.DeleteAsync("/api/media/install"); Assert.True(cancel.IsSuccessStatusCode);
        await Finished(service); await process.Stopped.Task.WaitAsync(TestBudget.Hang);
        status = JsonNode.Parse(await http.GetStringAsync("/api/media/install"))!;
        Assert.Null(status["running"]); Assert.Equal("cancelled", status["lastReceipt"]!["state"]!.GetValue<string>());
        await app.StopAsync();
    }

    // A deadline, not an iteration count: the budget is TestBudget.Hang whatever the
    // machine is doing, a passing wait resolves on the snapshot, and Assert.Null names
    // the install that was still running when the budget ran out.
    private static async Task Finished(MediaInstallService service)
    {
        var clock = Stopwatch.StartNew();
        while (service.Snapshot().Running != null && clock.Elapsed < TestBudget.Hang) await Task.Delay(20);
        Assert.Null(service.Snapshot().Running);
    }
    private sealed class FakeInstall
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MediaInstaller.Receipt> Run(MediaInstallRequest request, Action<string> log, CancellationToken ct)
        {
            log("fake process running"); Entered.TrySetResult();
            try { await Finish.Task.WaitAsync(ct); } // timing-under-test: the bound is the linked install token, which is what cancellation must reach
            catch (OperationCanceledException) { Stopped.TrySetResult(); throw; }
            return new(request.OptionId, DateTimeOffset.UtcNow.ToString("o"), true, null, new[] { "fake completed" }, null, null);
        }
    }
}
