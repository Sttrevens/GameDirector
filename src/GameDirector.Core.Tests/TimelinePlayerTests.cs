using GameDirector.Core.Adapters;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using GameDirector.Core.Playback;
using Xunit;

namespace GameDirector.Core.Tests;

public sealed class TimelinePlayerTests
{
    private static (TimelinePlayer player, RecordingAdapter adapter) Build()
    {
        var compiled = TimelineCompiler.Compile(TestAssets.ValidTimeline(), TestAssets.Manifest()).Timeline!;
        var adapter = new RecordingAdapter();
        return (new TimelinePlayer(compiled, adapter), adapter);
    }

    [Fact]
    public void Cues_at_t_zero_fire_immediately_on_play()
    {
        var (player, adapter) = Build();
        player.Play();
        Assert.Contains(adapter.Calls, c => c.StartsWith("shot:"));
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    public void Cues_fire_in_compiled_order()
    {
        var (player, adapter) = Build();
        player.Play();
        while (player.State == PlayerState.Playing) player.Tick(0.25);

        var spawnIdx = adapter.Calls.FindIndex(c => c.StartsWith("spawn:sentinel"));
        var animIdx = adapter.Calls.FindIndex(c => c == "anim:sentinel|RageExpose|0.2");
        Assert.True(spawnIdx >= 0 && animIdx > spawnIdx, "spawn must precede sentinel anim");
    }

    [Fact]
    public void Timescale_restore_fires_at_authored_time_plus_duration()
    {
        var (player, adapter) = Build();
        player.Play();
        while (player.State == PlayerState.Playing) player.Tick(0.1);
        var slow = adapter.Calls.FindIndex(c => c == "timescale:0.3");
        var restore = adapter.Calls.FindIndex(c => c == "timescale:1");
        Assert.True(slow >= 0 && restore > slow, "injected restore must follow the slow-mo cue");
    }

    [Fact]
    public void Determinism_different_dt_partitions_produce_identical_event_sequences()
    {
        List<string> Run(IEnumerable<double> dts)
        {
            var (player, _) = Build();
            player.Play();
            foreach (var dt in dts) { player.Tick(dt); if (player.State != PlayerState.Playing) break; }
            // drain to finish
            while (player.State == PlayerState.Playing) player.Tick(1.0);
            return player.Events.Select(e => e.Type + "|" + e.CueIndex + "|" + e.Summary).ToList();
        }

        var fixedTenth = Run(Enumerable.Repeat(0.1, 200));
        var lumpy = Run(new[] { 0.33, 0.02, 1.7, 0.11, 2.4, 0.5, 3.3, 0.9 });
        Assert.Equal(fixedTenth, lumpy);
    }

    [Fact] public void AdvanceToIgnoresEarlierAndNonfiniteTargetsAndHonorsPauseAndFinish()
    {
        var (player, adapter) = Build(); player.Play(); player.AdvanceTo(1);
        var fired = adapter.Calls.Count;
        foreach (var target in new[] { .5, -1, double.NaN, double.PositiveInfinity }) player.AdvanceTo(target);
        Assert.Equal(1, player.Time); Assert.Equal(fired, adapter.Calls.Count); Assert.Equal(PlayerState.Playing, player.State);
        player.Pause(); player.AdvanceTo(2); Assert.Equal(1, player.Time);
        player.Resume(); player.AdvanceTo(2); Assert.Equal(2, player.Time);
        player.AdvanceTo(999); Assert.Equal(6, player.Time); Assert.Equal(PlayerState.Finished, player.State);
        player.AdvanceTo(0); Assert.Equal(6, player.Time);
    }

    [Theory] [InlineData(24)] [InlineData(25)] [InlineData(30)] [InlineData(60)]
    public void ExactFrameAdvancesDispatchTheSameCuesAsTickWithoutAccumulatingTime(int fps)
    {
        var (absolute, absoluteAdapter) = Build(); var (tick, tickAdapter) = Build(); absolute.Play(); tick.Play();
        for (int i = 1; i <= 6 * fps; i++) {
            absolute.AdvanceTo(FrameClock.TimeOf(i, fps)); tick.Tick(1.0 / fps);
            Assert.Equal((double)i / fps, absolute.Time); Assert.Equal(tickAdapter.Calls, absoluteAdapter.Calls);
        }
        tick.Tick(Math.Max(0, tick.Duration - tick.Time)); // legacy take completion drains any fractional tail
        Assert.Equal(PlayerState.Finished, absolute.State); Assert.Equal(PlayerState.Finished, tick.State);
        Assert.Equal(tick.Events.Select(e => (e.Time, e.Type, e.CueIndex, e.Summary)), absolute.Events.Select(e => (e.Time, e.Type, e.CueIndex, e.Summary)));
        Assert.Equal(new[] { 0.0, .5, 2, 2.5, 3, 4, 5, 5, 6 }, absolute.Events.Select(e => e.DispatchedAt));
    }

    [Fact] public void EpsilonCloseFutureCueDoesNotAdvancePastTheRequestedFrameOrRewindPresentation()
    {
        var asset = new TimelineAsset { Id = "epsilon", Cues = new() {
            new Cue { T = .5 + .0000000005, Type = CueTypes.Marker, Label = "close" },
            new Cue { T = .5 + .000000002, Type = CueTypes.Marker, Label = "later" },
            new Cue { T = 1, Type = CueTypes.Marker, Label = "end" } } };
        var adapter = new ClockProbe(); var player = new TimelinePlayer(TimelineCompiler.Compile(asset, TestAssets.Manifest()).Timeline!, adapter);
        player.Play(); player.AdvanceTo(.5);
        Assert.Equal(.5, player.Time); Assert.Single(player.Events); Assert.Equal(.5, player.Events[0].DispatchedAt);
        player.AdvanceTo(.4); Assert.Equal(.5, player.Time);
        player.AdvanceTo(.75); Assert.Equal(2, player.Events.Count);
        Assert.Equal(adapter.Times.OrderBy(t => t), adapter.Times);
        Assert.Equal(.75, adapter.Times.Last()); Assert.Equal(.75, adapter.Elapsed);
    }

    private sealed class ClockProbe : IGameDirectorAdapter, IDirectorSessionAdapter
    {
        public readonly List<double> Times = new(); public double Elapsed;
        public void BeginSession() {} public void EndSession() {}
        public void AdvancePresentation(double dt, double t) { Times.Add(t); Elapsed += dt; }
        public void ApplyCameraShot(ShotSpec s) {} public void PlayAnimation(string r,string c,float f) {}
        public void MoveRole(string r,string l,float s,string h) {} public void FaceRole(string r,string h) {}
        public void SpawnRole(string r,string l,string h) {} public void DespawnRole(string r) {}
        public void PlayAudio(string a,float v) {} public void StopAudio(string a) {} public void SetTimeScale(float s) {} public void Marker(string l) {}
    }

    [Fact]
    public void Pause_freezes_the_playhead()
    {
        var (player, _) = Build();
        player.Play();
        player.Tick(1.0);
        double t = player.Time;
        int fired = player.Events.Count;
        player.Pause();
        player.Tick(5.0);
        Assert.Equal(t, player.Time);
        Assert.Equal(fired, player.Events.Count);
        player.Resume();
        player.Tick(0.5);
        Assert.True(player.Time > t);
    }

    [Fact]
    public void Timeline_reaches_finished_state()
    {
        var (player, _) = Build();
        player.Play();
        while (player.State == PlayerState.Playing) player.Tick(0.5);
        Assert.Equal(PlayerState.Finished, player.State);
    }

    [Fact]
    public void Stop_suppresses_remaining_cues()
    {
        var (player, adapter) = Build();
        player.Play();
        player.Tick(1.0);
        player.Stop();
        int fired = adapter.Calls.Count;
        player.Tick(10.0);
        Assert.Equal(fired, adapter.Calls.Count);
        Assert.Equal(PlayerState.Stopped, player.State);
    }
}
