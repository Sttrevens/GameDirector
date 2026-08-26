using System.Collections.Generic;

namespace GameDirector.Core.Dsl
{
    /// <summary>
    /// The game self-description (L2). Served live by the bridge endpoint and/or
    /// checked into the adapter folder for offline validation. The LLM is only
    /// allowed to compose from what is declared here; the compiler enforces that.
    /// </summary>
    public sealed class CapabilityManifest
    {
        public string Game { get; set; }
        public string GameVersion { get; set; }
        public string ManifestVersion { get; set; } = "0.1";

        public List<RoleDescriptor> Roles { get; set; } = new List<RoleDescriptor>();
        public List<ActorDescriptor> Actors { get; set; } = new List<ActorDescriptor>();
        public List<LocationDescriptor> Locations { get; set; } = new List<LocationDescriptor>();
        public List<AudioDescriptor> Audio { get; set; } = new List<AudioDescriptor>();

        /// <summary>Shot vocabulary this game's adapter implements (e.g. lockoff/dolly/orbit/tracking/crane).</summary>
        public List<string> ShotTypes { get; set; } = new List<string>();

        /// <summary>Framing vocabulary (e.g. extreme-closeup/closeup/medium/full/wide).</summary>
        public List<string> FrameTypes { get; set; } = new List<string>();

        /// <summary>Feature flags, e.g. "capture.video"="avpro", "director.mode"="offline-sandbox".</summary>
        public Dictionary<string, string> Capabilities { get; set; } = new Dictionary<string, string>();

        public RoleDescriptor FindRole(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Roles.Count; i++) if (Roles[i].Id == id) return Roles[i];
            return null;
        }

        public ActorDescriptor FindActor(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Actors.Count; i++) if (Actors[i].Id == id) return Actors[i];
            return null;
        }

        public LocationDescriptor FindLocation(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Locations.Count; i++) if (Locations[i].Id == id) return Locations[i];
            return null;
        }

        public AudioDescriptor FindAudio(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Audio.Count; i++) if (Audio[i].Id == id) return Audio[i];
            return null;
        }
    }

    public sealed class RoleDescriptor
    {
        public string Id { get; set; }
        /// <summary>player | npc | monster | prop</summary>
        public string Kind { get; set; }
        public string DisplayName { get; set; }
        /// <summary>Actor id that supplies this role's animation clips.</summary>
        public string DefaultActor { get; set; }
        /// <summary>True when the role exists in the scene before any spawn cue.</summary>
        public bool PresentAtStart { get; set; }
        /// <summary>Free-form adapter hint, e.g. "bind:tag:Player" or "spawn:prefab:BigGuai".</summary>
        public string BindingHint { get; set; }
    }

    public sealed class ActorDescriptor
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public List<string> Clips { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
    }

    public sealed class LocationDescriptor
    {
        public string Id { get; set; }
        /// <summary>Logical space/scene this location belongs to (e.g. "grimforest").</summary>
        public string Space { get; set; }
        public float[] Position { get; set; }
        public float HeadingDeg { get; set; }
    }

    public sealed class AudioDescriptor
    {
        public string Id { get; set; }
        /// <summary>bgm | sfx | amb</summary>
        public string Kind { get; set; }
    }
}
