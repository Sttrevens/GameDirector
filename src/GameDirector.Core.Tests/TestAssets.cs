using GameDirector.Core.Dsl;

namespace GameDirector.Core.Tests;

internal static class TestAssets
{
    public static CapabilityManifest Manifest() => new CapabilityManifest
    {
        Game = "TestGame",
        GameVersion = "0",
        ShotTypes = new List<string> { "lockoff", "dolly", "orbit", "tracking", "crane" },
        FrameTypes = new List<string> { "extreme-closeup", "closeup", "medium", "full", "wide" },
        Roles = new List<RoleDescriptor>
        {
            new RoleDescriptor { Id = "hero", Kind = "player", DefaultActor = "streamer", PresentAtStart = true },
            new RoleDescriptor { Id = "bigguai", Kind = "monster", DefaultActor = "bigguai", PresentAtStart = false },
        },
        Actors = new List<ActorDescriptor>
        {
            new ActorDescriptor { Id = "streamer", Clips = new List<string> { "Idle", "Walk", "Cheer" } },
            new ActorDescriptor { Id = "bigguai", Clips = new List<string> { "Idle", "RageExpose", "Tornado", "ConfidencePose" } },
        },
        Locations = new List<LocationDescriptor>
        {
            new LocationDescriptor { Id = "loc_gate", Space = "test", Position = new float[] { 0, 0, 0 } },
            new LocationDescriptor { Id = "loc_stage", Space = "test", Position = new float[] { 10, 0, 0 } },
            new LocationDescriptor { Id = "cam_north", Space = "test", Position = new float[] { 0, 3, -8 } },
        },
        Audio = new List<AudioDescriptor>
        {
            new AudioDescriptor { Id = "bgm_dark", Kind = "bgm" },
            new AudioDescriptor { Id = "sfx_roar", Kind = "sfx" },
        }
    };

    public static Cue Shot(double t, string subject = "hero", string type = "lockoff", string frame = "medium",
        string from = "cam_north", double duration = 3) => new Cue
    {
        T = t,
        Type = CueTypes.CameraShot,
        Shot = new ShotSpec { Type = type, Subject = subject, Frame = frame, From = from, DurationSeconds = duration }
    };

    public static TimelineAsset ValidTimeline() => new TimelineAsset
    {
        Id = "t_valid",
        Title = "valid",
        Cues = new List<Cue>
        {
            Shot(0),
            new Cue { T = 0.5, Type = CueTypes.ActorAnim, Role = "hero", Clip = "Idle" },
            new Cue { T = 2.0, Type = CueTypes.ActorSpawn, Role = "bigguai", Location = "loc_gate", HeadingTo = "hero" },
            new Cue { T = 2.5, Type = CueTypes.ActorAnim, Role = "bigguai", Clip = "RageExpose" },
            new Cue { T = 3.0, Type = CueTypes.ActorMove, Role = "bigguai", To = "loc_stage", Speed = 2.5f },
            new Cue { T = 4.0, Type = CueTypes.WorldTimeScale, Scale = 0.3f, Duration = 1.0 },
            new Cue { T = 5.0, Type = CueTypes.AudioPlay, AudioId = "bgm_dark", Volume = 0.6f },
            new Cue { T = 6.0, Type = CueTypes.Marker, Label = "beat" },
        }
    };
}
