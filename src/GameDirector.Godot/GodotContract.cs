using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

// Implementation coverage, not another vocabulary/default table. Core owns every meaning.
public static class GodotContract
{
    public static readonly string[] Parameters = { "targetHeight", "coverage", "distance", "offsetX", "offsetY", "offsetZ", "toOffsetX", "toOffsetY", "toOffsetZ", "orbitDeg", "tilt", "pan", "roll", "endFov" };
    public static object Bindings => new {
        cues = new { cameraShot = CueTypes.CameraShot, actorSpawn = CueTypes.ActorSpawn, actorDespawn = CueTypes.ActorDespawn,
            actorAnim = CueTypes.ActorAnim, actorMove = CueTypes.ActorMove, actorFace = CueTypes.ActorFace, worldTimescale = CueTypes.WorldTimeScale, marker = CueTypes.Marker },
        manifestVersion = new CapabilityManifest().ManifestVersion,
        parameters = Parameters,
        capabilities = new { spawn = CapabilityKeys.ActorSpawn, fade = CapabilityKeys.AnimationFade, mode = CapabilityKeys.DirectorMode },
        spawnSupported = CapabilityKeys.SpawnSupported, crossfade = CapabilityKeys.FadeCrossfade
    };
    public static void Stamp(CapabilityManifest m, string sourceRoot, string fingerprint)
    {
        var refusal = EngineContract.Current.Version.Refusal(1, 2);
        if (refusal != null) throw new InvalidOperationException(refusal);
        foreach (var name in Parameters) if (ShotVocabulary.FindParam(name) == null) throw new InvalidOperationException("unrecognized implemented camera parameter: " + name);
        var c = m.Capabilities;
        c[CapabilityKeys.Engine] = GodotDefaults.Engine;
        c[CapabilityKeys.ProjectSourceRoot] = sourceRoot;
        c[CapabilityKeys.PresentationSourceFingerprint] = fingerprint;
        c[CapabilityKeys.DirectorMode] = CapabilitySpecs.Find(CapabilityKeys.DirectorMode).Values.Single().Value;
        c[CapabilityKeys.Measure] = CapabilityKeys.MeasureV1;
        c[CapabilityKeys.CaptureAudio] = CapabilitySpecs.Find(CapabilityKeys.CaptureAudio).Values.Single().Value;
        c[CapabilityKeys.CameraParams] = string.Join(CapabilitySpecs.Find(CapabilityKeys.CameraParams).Separator, Parameters);
    }
}
