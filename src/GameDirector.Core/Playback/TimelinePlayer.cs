using System;
using System.Collections.Generic;
using GameDirector.Core.Adapters;
using GameDirector.Core.Dsl;

namespace GameDirector.Core.Playback
{
    public enum PlayerState { Idle, Playing, Paused, Finished, Stopped, Failed }

    /// <summary>One dispatched event, recorded for logs, tests, and the review loop.</summary>
    public struct DirectorEvent
    {
        public double Time;
        public double DispatchedAt;        // timeline time when dispatched
        public int CueIndex;       // authored source index, -1 for injected cues
        public string Type;
        public string Summary;

        public override string ToString() => "t=" + Time.ToString("0.###") + " [" + Type + "] " + Summary;
    }

    /// <summary>
    /// Deterministic timeline executor. The caller owns time: every Tick(dt)
    /// advances the playhead and fires all cues whose time has been reached,
    /// in compiled order. Same compiled timeline + same dt sequence =&gt; same
    /// event sequence. No wall-clock, no engine API, no threads.
    /// </summary>
    public sealed class TimelinePlayer
    {
        private readonly CompiledTimeline _timeline;
        private readonly IGameDirectorAdapter _adapter;
        private readonly List<DirectorEvent> _events = new List<DirectorEvent>();
        private int _nextCue;
        private double _time;
        private bool _ownsSession;
        public string Failure { get; private set; }
        private IDirectorSessionAdapter Session => _adapter as IDirectorSessionAdapter;

        public PlayerState State { get; private set; } = PlayerState.Idle;
        public double Time => _time;
        public double Duration => _timeline.Duration;
        public IReadOnlyList<DirectorEvent> Events => _events;

        public TimelinePlayer(CompiledTimeline timeline, IGameDirectorAdapter adapter)
        {
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public void Play()
        {
            if (State == PlayerState.Playing) return;
            if (State == PlayerState.Paused) { State = PlayerState.Playing; return; }
            EndSession();
            _ownsSession = true;
            try { Session?.BeginSession(); }
            catch (Exception ex) { Fail(ex); return; }
            Failure = null;
            _nextCue = 0;
            _time = 0;
            _events.Clear();
            State = PlayerState.Playing;
            try {
                DispatchDue(); // cues at t=0 fire immediately
                Session?.AdvancePresentation(0, 0);
            } catch (Exception ex) { Fail(ex); }
        }

        public void Pause() { if (State == PlayerState.Playing) State = PlayerState.Paused; }
        public void Resume() { if (State == PlayerState.Paused) State = PlayerState.Playing; }

        public void Stop()
        {
            State = PlayerState.Stopped;
            _nextCue = _timeline.OrderedCues.Count;
            EndSession();
        }

        /// <summary>Advance by dt seconds (caller-chosen; use unscaled time in engines).</summary>
        public void Tick(double dt)
        {
            if (State != PlayerState.Playing) return;
            if (dt < 0 || double.IsNaN(dt) || double.IsInfinity(dt)) return;
            try
            {
                double target = Math.Min(_timeline.Duration, _time + dt);
                while (_nextCue < _timeline.OrderedCues.Count && _timeline.OrderedCues[_nextCue].T <= target + 1e-9)
                {
                    double boundary = _timeline.OrderedCues[_nextCue].T;
                    Advance(Math.Max(_time, boundary));
                    DispatchDue();
                }
                Advance(target);
                if (_nextCue >= _timeline.OrderedCues.Count && _time >= _timeline.Duration) {
                    State = PlayerState.Finished;
                    EndSession();
                }
            } catch (Exception ex) { Fail(ex); }
        }

        private void Advance(double target)
        {
            double delta = Math.Max(0, target - _time);
            _time = target;
            Session?.AdvancePresentation(delta, _time);
        }

        private void EndSession()
        {
            if (!_ownsSession) return;
            _ownsSession = false;
            try { Session?.EndSession(); }
            catch (Exception ex) { Failure = ex.Message; State = PlayerState.Failed; }
        }

        private void Fail(Exception ex)
        {
            Failure = ex.Message;
            State = PlayerState.Failed;
            EndSession();
        }

        private void DispatchDue()
        {
            var cues = _timeline.OrderedCues;
            while (_nextCue < cues.Count && cues[_nextCue].T <= _time + 1e-9)
            {
                Dispatch(cues[_nextCue]);
                _nextCue++;
            }
        }

        private void Dispatch(Cue cue)
        {
            string summary;
            switch (cue.Type)
            {
                case CueTypes.CameraShot:
                    _adapter.ApplyCameraShot(cue.Shot);
                    summary = "shot " + (cue.Shot != null ? cue.Shot.Type + "/" + cue.Shot.Frame + " on " + cue.Shot.Subject : "<null>");
                    break;
                case CueTypes.ActorSpawn:
                    _adapter.SpawnRole(cue.Role, cue.Location, cue.HeadingTo);
                    summary = "spawn " + cue.Role + " @ " + cue.Location;
                    break;
                case CueTypes.ActorDespawn:
                    _adapter.DespawnRole(cue.Role);
                    summary = "despawn " + cue.Role;
                    break;
                case CueTypes.ActorAnim:
                    _adapter.PlayAnimation(cue.Role, cue.Clip, cue.Fade);
                    summary = "anim " + cue.Role + " -> " + cue.Clip;
                    break;
                case CueTypes.ActorMove:
                    _adapter.MoveRole(cue.Role, cue.To, cue.Speed, cue.HeadingTo);
                    summary = "move " + cue.Role + " -> " + cue.To;
                    break;
                case CueTypes.ActorFace:
                    _adapter.FaceRole(cue.Role, cue.HeadingTo);
                    summary = "face " + cue.Role + " -> " + cue.HeadingTo;
                    break;
                case CueTypes.AudioPlay:
                    _adapter.PlayAudio(cue.AudioId, cue.Volume);
                    summary = "audio " + cue.AudioId + " vol=" + cue.Volume.ToString("0.##");
                    break;
                case CueTypes.AudioStop:
                    _adapter.StopAudio(cue.AudioId);
                    summary = "audio stop " + cue.AudioId;
                    break;
                case CueTypes.WorldTimeScale:
                    _adapter.SetTimeScale(cue.Scale);
                    summary = "timescale " + cue.Scale.ToString("0.##");
                    break;
                case CueTypes.Marker:
                    _adapter.Marker(cue.Label);
                    summary = "marker " + cue.Label;
                    break;
                default:
                    summary = "ignored unknown cue " + cue.Type;
                    break;
            }
            _events.Add(new DirectorEvent { Time = cue.T, DispatchedAt = _time, CueIndex = cue.SourceIndex, Type = cue.Type, Summary = summary });
        }
    }
}
