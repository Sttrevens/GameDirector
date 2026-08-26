using GameDirector.Core.Adapters;
using GameDirector.Core.Compilation;
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

        var spawnIdx = adapter.Calls.FindIndex(c => c.StartsWith("spawn:bigguai"));
        var animIdx = adapter.Calls.FindIndex(c => c == "anim:bigguai|RageExpose|0.2");
        Assert.True(spawnIdx >= 0 && animIdx > spawnIdx, "spawn must precede bigguai anim");
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
