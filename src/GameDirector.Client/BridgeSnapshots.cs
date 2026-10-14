using System.Text.Json;
using GameDirector.Core.Capture;

namespace GameDirector.Client;

// Engines share the receipt; their playback event/review payloads differ.
// Optional fields retain both Unity and Three facts without callers parsing JSON.
public sealed class BridgeEvent
{
    public double? Time { get; set; }
    public double? T { get; set; }
    public double? DispatchedAt { get; set; }
    public int? CueIndex { get; set; }
    public string? Type { get; set; }
    public string? Summary { get; set; }
    public string? Role { get; set; }
    public string? Label { get; set; }
}
public sealed class TakeSnapshot
{
    public TakeReceipt? Take { get; set; }
    public BridgeEvent[]? Events { get; set; }
    public JsonElement[]? Reviews { get; set; }
    public JsonElement[]? Measures { get; set; }
}
public sealed class StatusSnapshot
{
    public string? State { get; set; }
    public double? Time { get; set; }
    public double? Duration { get; set; }
    public string? Error { get; set; }
    public TakeReceipt? Take { get; set; }
    public BridgeEvent[]? RecentEvents { get; set; }
    public BridgeEvent[]? Events { get; set; }
    public JsonElement[]? Reviews { get; set; }
    public JsonElement[]? Measures { get; set; }
}
