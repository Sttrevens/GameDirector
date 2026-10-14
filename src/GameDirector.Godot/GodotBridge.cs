using System.Net;
using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameDirector.Godot;

public sealed partial class GodotBridge : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly GodotSession session;
    private readonly GodotDockOwner? dockOwner;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Task lease = Task.CompletedTask;
    private int pending;
    private volatile bool stopping;
    public string Endpoint { get; private set; } = "";
    private GodotBridge(WebApplication app, IGodotWorkerFactory factory, GodotDockOwner? dockOwner)
    { this.app = app; session = new(factory); this.dockOwner = dockOwner; }
    public static async Task<GodotBridge> Start(IGodotWorkerFactory factory, int? port = null, CancellationToken ct = default, GodotDockOwner? dockOwner = null)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port ?? GodotDefaults.Port));
        var app = builder.Build(); var bridge = new GodotBridge(app, factory, dockOwner); app.Run(bridge.Handle);
        await app.StartAsync(ct);
        bridge.Endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        bridge.lease = bridge.Lease(); return bridge;
    }
    private async Task Handle(HttpContext context)
    {
        var request = context.Request; var path = request.Path.Value; var post = request.Method == HttpMethods.Post;
        try {
            var host = request.Host.Value; var port = new Uri(Endpoint).Port;
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress!) || host != BridgeDefaults.LoopbackHost + ":" + port && host != BridgeDefaults.LoopbackAlias + ":" + port)
                throw new BridgeRequestException(BridgeErrors.Forbidden, "Host header must be the loopback bridge address");
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            if (site.Length != 0 && site != "none" && site != "same-origin") throw new BridgeRequestException(BridgeErrors.Forbidden, "cross-site bridge requests rejected");
            if ((post || path == "/" + GodotDefaults.OwnerRoute) && request.Headers.ContainsKey("Origin")) throw new BridgeRequestException(BridgeErrors.Forbidden, "browser-origin bridge requests rejected");
            if (request.Method != HttpMethods.Get && !post) throw new BridgeRequestException(BridgeErrors.MethodNotAllowed, "method not allowed");
            RefuseInteractive(path); // After caller security, before body admission (stop has no body).
            if (post && !string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                throw new BridgeRequestException(BridgeErrors.UnsupportedMediaType, "application/json required");
            if (!post && path == "/" + BridgeRoutes.Health) { await Json(context, new { ok = true, engine = GodotDefaults.Engine }); return; }
            if (!post && path == "/" + BridgeRoutes.Contract) { await Json(context, EngineContract.Current); return; }
            if (!post && path == "/" + GodotDefaults.OwnerRoute && dockOwner != null) {
                var challenge = request.Query["challenge"].ToString();
                if (!dockOwner.Verify(challenge, request.Query["proof"].ToString()))
                    throw new BridgeRequestException(BridgeErrors.Forbidden, "dock owner proof rejected");
                await Json(context, dockOwner.Prove(challenge)); return;
            }
            if (stopping) throw new BridgeRequestException(BridgeErrors.Busy, "bridge is shutting down");
            if (Interlocked.Increment(ref pending) > BridgeLimits.MaxQueuedRequests) { Interlocked.Decrement(ref pending); throw new BridgeRequestException(BridgeErrors.Busy, "queue full"); }
            try {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(path == "/" + BridgeRoutes.TakeStart ? BridgeDefaults.TakeStartTimeoutSeconds : BridgeBudgets.EngineWaitSeconds));
                var data = post ? await Read(request, budget.Token) : default;
                await gate.WaitAsync(budget.Token);
                try { if (stopping) throw new BridgeRequestException(BridgeErrors.Busy, "bridge is shutting down"); await Route(context, data, budget.Token); }
                finally { gate.Release(); }
            } finally { Interlocked.Decrement(ref pending); }
        } catch (Exception ex) {
            var error = ex is OperationCanceledException ? BridgeErrors.Timeout : BridgeRequestException.Classify(ex);
            if (!context.Response.HasStarted) { context.Response.StatusCode = error.Status; await Json(context, new { error = ex.Message, code = error.Code }); }
            else context.Abort();
        }
    }
    private async Task Lease()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) {
            if (pending != 0 || !await gate.WaitAsync(0, lifetime.Token)) continue;
            try { await session.ExpireLease(); } finally { gate.Release(); }
        } } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        stopping = true; lifetime.Cancel(); await lease;
        await gate.WaitAsync(); try { await session.DisposeAsync(); } finally { gate.Release(); }
        await app.StopAsync(); await app.DisposeAsync(); lifetime.Dispose(); gate.Dispose();
    }
    private static Task Json(HttpContext c, object value)
    {
        c.Response.ContentType = "application/json"; c.Response.Headers.CacheControl = "no-store";
        return c.Response.WriteAsync(DslJson.Serialize(value));
    }
}
