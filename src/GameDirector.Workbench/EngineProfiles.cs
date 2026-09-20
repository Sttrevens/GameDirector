using GameDirector.Core.Dsl;

namespace GameDirector.Workbench;

/// <summary>One engine family the Workbench knows how to talk to. The default
/// endpoint is a convention shared with the engine's own adapter; users can
/// always override it per project. The web UI renders this catalog verbatim
/// instead of keeping its own engine→port table.</summary>
public sealed record EngineProfile(string Id, string Label, string? DefaultEndpoint);

public static class EngineProfiles
{
    public const string CustomId = "Custom";
    public static readonly IReadOnlyList<EngineProfile> All = new[]
    {
        new EngineProfile("Unity", "Unity", BridgeDefaults.UnityEndpoint),
        new EngineProfile("Three.js", "Three.js", BridgeDefaults.ThreeEndpoint),
        new EngineProfile(CustomId, "Custom", null),
    };
    public static EngineProfile? Find(string id) => All.FirstOrDefault(e => e.Id == id);
}
