using System.Text.Json;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

public sealed record WorkerFrame(byte[]? Png, JsonElement? Review = null, JsonElement? Pose = null, JsonElement? Measure = null);
public interface IGodotWorker : IAsyncDisposable
{
    Task Begin(CompiledTimeline timeline, int width, int height, CancellationToken ct);
    Task<WorkerFrame> Frame(int index, int fps, bool advanceOnly, CancellationToken ct, bool measure = false);
    Task<JsonElement> Finish(CancellationToken ct);
}
public interface IGodotWorkerFactory
{
    CapabilityManifest Manifest { get; }
    void AssertFresh();
    Task<IGodotWorker> Open(CancellationToken ct);
}

// Convention only; not a contract version change. Every Godot entrypoint accepts --port.
public static class GodotDefaults
{
    private static JsonElement Defaults { get; } = ReadDefaults();
    public static int Port { get; } = Defaults.GetProperty("port").GetInt32();
    public static string OwnerRoute { get; } = Defaults.GetProperty("ownerRoute").GetString()!;
    public static string OwnerEnvironment { get; } = Defaults.GetProperty("ownerEnvironment").GetString()!;
    public static string LogOption { get; } = Defaults.GetProperty("logOption").GetString()!;
    private static JsonElement ReadDefaults()
    {
        using var stream = typeof(GodotDefaults).Assembly.GetManifestResourceStream("GameDirector.Godot.bridge_defaults.json")!;
        using var defaults = JsonDocument.Parse(stream);
        return defaults.RootElement.Clone();
    }
    public const string Engine = "godot";
}
