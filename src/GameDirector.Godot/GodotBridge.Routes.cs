using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Microsoft.AspNetCore.Http;

namespace GameDirector.Godot;

public sealed partial class GodotBridge
{
    private static void RefuseInteractive(string? path)
    {
        if (path == "/" + BridgeRoutes.TimelinePlay || path == "/" + BridgeRoutes.TimelineStop || path == "/" + BridgeRoutes.Capture)
            throw new BridgeRequestException(BridgeErrors.StateConflict, "Interactive play/stop/capture is unsupported on Godot; use frame-stepped gd take or gd measure");
    }
    private async Task Route(HttpContext c, JsonElement data, CancellationToken ct)
    {
        var path = c.Request.Path.Value;
        if (c.Request.Method == HttpMethods.Get) {
            if (path == "/" + BridgeRoutes.Manifest) { await Json(c, session.Manifest); return; }
            if (path == "/" + BridgeRoutes.Take || path == "/" + BridgeRoutes.Status) { await Json(c, session.Snapshot()); return; }
        } else {
            if (path == "/" + BridgeRoutes.TakeStart) {
                if (!data.TryGetProperty("timeline", out var timeline) || timeline.ValueKind != JsonValueKind.Object ||
                    !Integer(data, "frameRate") || !Integer(data, "width") || !Integer(data, "height") || !Integer(data, "captureFromFrame", optional: true))
                    throw new BridgeRequestException(BridgeErrors.InvalidRequest, "take timeline and Int32 geometry/capture start are required");
                await Json(c, await session.Start(Parse<TakeRequest>(data), ct)); return;
            }
            if (path == "/" + BridgeRoutes.TakeFrame) {
                if (!Integer(data, "frameIndex") || !Boolean(data, "advanceOnly") || !Boolean(data, "measure"))
                    throw new BridgeRequestException(BridgeErrors.InvalidRequest, "frame index must be an Int32; modes must be booleans");
                var png = await session.Frame(Parse<FrameRequest>(data), ct);
                if (png == null) { c.Response.StatusCode = StatusCodes.Status204NoContent; return; }
                c.Response.ContentType = "image/png"; c.Response.ContentLength = png.Length; await c.Response.Body.WriteAsync(png, ct); return;
            }
            if (path == "/" + BridgeRoutes.TakeStop) { await session.Stop(Parse<TakeStopRequest>(data)); await Json(c, new { ok = true }); return; }
            if (session.Capturing) throw new BridgeRequestException(BridgeErrors.Busy, "bridge busy capturing a take");
        }
        throw new BridgeRequestException(BridgeErrors.NotFound, "unsupported route");
    }
    private static bool Integer(JsonElement data, string key, bool optional = false) => data.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _) : optional;
    private static bool Boolean(JsonElement data, string key) => !data.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static T Parse<T>(JsonElement data)
    {
        try { return DslJson.Deserialize<T>(data.GetRawText()); }
        catch (JsonException ex) { throw new BridgeRequestException(BridgeErrors.InvalidRequest, ex.Message); }
    }
    private static async Task<JsonElement> Read(HttpRequest request, CancellationToken ct)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = await request.Body.ReadAsync(buffer, ct)) > 0) {
            if (bytes.Length + n > BridgeLimits.MaxRequestBytes) throw new BridgeRequestException(BridgeErrors.PayloadTooLarge, "request body too large");
            bytes.Write(buffer, 0, n);
        }
        try {
            using var doc = JsonDocument.Parse(bytes.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new BridgeRequestException(BridgeErrors.InvalidRequest, "request must be a JSON object");
            return doc.RootElement.Clone();
        } catch (JsonException ex) { throw new BridgeRequestException(BridgeErrors.InvalidRequest, ex.Message); }
    }
}
