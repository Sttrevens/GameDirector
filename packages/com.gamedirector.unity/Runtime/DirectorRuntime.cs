using System.Collections.Generic;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using GameDirector.Core.Playback;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Hosts the deterministic TimelinePlayer inside play mode. Ticks with
    /// unscaled delta time so world.timescale slow-mo never distorts cue timing.
    /// Owns the event ring buffer surfaced by /status for the review loop.
    ///
    /// Multi-instance note: this component is deliberately scene-scoped — it
    /// holds no static state. The active instance is created by whoever owns
    /// director mode in the host project (see adapters/cdrebirth).
    /// </summary>
    public sealed class DirectorRuntime : MonoBehaviour
    {
        [SerializeField] private GameDirectorAdapterBase adapter;
        [SerializeField] private int statusEventBufferSize = 32;

        private TimelinePlayer _player;
        private readonly LinkedList<string> _recentEvents = new LinkedList<string>();

        public CapabilityManifest Manifest => adapter != null ? adapter.Manifest : null;
        public PlayerState? State => _player?.State;
        public double Time => _player?.Time ?? 0;
        public double Duration => _player?.Duration ?? 0;

        private void Awake()
        {
            if (adapter == null) adapter = GetComponent<GameDirectorAdapterBase>();
            if (adapter == null) adapter = gameObject.AddComponent<GenericSceneAdapter>();
        }

        private void Update()
        {
            if (_player == null || _player.State != PlayerState.Playing) return;
            _player.Tick(Time.unscaledDeltaTime);
            DrainEvents();
        }

        /// <summary>Compile + play. Returns a diagnostics DTO; on errors nothing plays.</summary>
        public object PlayFromJson(string timelineJson)
        {
            var asset = BridgeJson.Deserialize<TimelineAsset>(timelineJson);
            if (Manifest == null) return new { ok = false, errors = new[] { "no adapter manifest bound" } };

            var result = TimelineCompiler.Compile(asset, Manifest);
            if (result.HasErrors)
            {
                var errors = new List<string>();
                foreach (var d in result.Diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) errors.Add(d.ToString());
                return new { ok = false, errors = errors.ToArray() };
            }

            _player = new TimelinePlayer(result.Timeline, adapter);
            _recentEvents.Clear();
            _drained = 0;
            _player.Play();
            DrainEvents();
            return new
            {
                ok = true,
                id = result.Timeline.Id,
                cues = result.Timeline.OrderedCues.Count,
                duration = result.Timeline.Duration,
                warnings = WarningsOf(result)
            };
        }

        public void Stop() => _player?.Stop();

        public byte[] CapturePng()
        {
            var rig = GetComponent<CinematicCameraRig>() ?? gameObject.AddComponent<CinematicCameraRig>();
            return FrameCapture.Capture(rig.DirectorCamera, 1280, 720);
        }

        public object GetStatusDto() => new
        {
            state = (_player?.State ?? PlayerState.Idle).ToString(),
            time = _player?.Time ?? 0,
            duration = _player?.Duration ?? 0,
            recentEvents = _recentEvents
        };

        // Events list is append-only per run; _drained tracks how many we copied.
        private int _drained;

        private void DrainEvents()
        {
            if (_player == null) return;
            var events = _player.Events;
            for (int i = _drained; i < events.Count; i++)
            {
                _recentEvents.AddLast(events[i].ToString());
                while (_recentEvents.Count > statusEventBufferSize) _recentEvents.RemoveFirst();
            }
            _drained = events.Count;
        }

        private static string[] WarningsOf(CompileResult result)
        {
            var list = new List<string>();
            foreach (var d in result.Diagnostics)
                if (d.Severity == DiagnosticSeverity.Warning) list.Add(d.ToString());
            return list.ToArray();
        }
    }
}
