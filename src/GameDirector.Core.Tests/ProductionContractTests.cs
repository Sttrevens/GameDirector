using GameDirector.Core.Adapters;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using GameDirector.Core.Playback;
using Xunit;

namespace GameDirector.Core.Tests;

public class ProductionContractTests
{
    [Fact] public void Compilation_detaches_all_authored_payloads()
    {
        var a = TestAssets.ValidTimeline();
        a.Cues[0].Shot.Params = new() { ["shake"] = .2f };
        var c = TimelineCompiler.Compile(a, TestAssets.Manifest()).Timeline!;
        a.Cues[0].Shot.Params["shake"] = 999;
        a.Cues[0].Shot.Subject = "changed";
        a.Cues[0].T = 99;
        Assert.Equal(-1, a.Cues[0].SourceIndex);
        Assert.Equal(0, c.OrderedCues[0].T);
        Assert.Equal("hero", c.OrderedCues[0].Shot.Subject);
        Assert.Equal(.2f, c.OrderedCues[0].Shot.Params["shake"]);
    }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void Nonfinite_values_never_reach_playback(double x)
    {
        foreach (var cue in new[] {
            new Cue { Type=CueTypes.WorldTimeScale, Scale=(float)x },
            new Cue { Type=CueTypes.WorldTimeScale, Duration=x },
            new Cue { Type=CueTypes.ActorMove, Role="hero", To="loc_gate", Speed=(float)x },
            new Cue { Type=CueTypes.ActorAnim, Role="hero", Clip="Idle", Fade=(float)x },
            new Cue { Type=CueTypes.CameraShot, Shot=new ShotSpec {Subject="hero", DurationSeconds=x} }
        }) Assert.True(TimelineCompiler.Compile(new TimelineAsset {Id="bad", Cues=new(){cue}}, TestAssets.Manifest()).HasErrors);
    }
    [Fact] public void Empty_or_malformed_manifest_is_closed()
    {
        var m = TestAssets.Manifest(); m.ShotTypes.Clear();
        Assert.True(TimelineCompiler.Compile(TestAssets.ValidTimeline(),m).HasErrors);
        m = TestAssets.Manifest(); m.Actors[0].Clips.Clear();
        Assert.True(TimelineCompiler.Compile(TestAssets.ValidTimeline(),m).HasErrors);
        m = TestAssets.Manifest(); m.Roles.Add(m.Roles[0]);
        Assert.True(TimelineCompiler.Compile(TestAssets.ValidTimeline(),m).HasErrors);
        m = TestAssets.Manifest(); m.Locations=null!;
        Assert.True(TimelineCompiler.Compile(TestAssets.ValidTimeline(),m).HasErrors);
    }
    [Fact] public void New_timescale_cancels_stale_restore_even_at_same_boundary()
    {
        foreach (var time in new[]{1.0,2.0}) {
            var a = new TimelineAsset {Id="scale", Cues=new() {
                new Cue {Type=CueTypes.WorldTimeScale, T=0, Scale=.3f,Duration=2},
                new Cue {Type=CueTypes.WorldTimeScale, T=time,Scale=.6f,Duration=3}
            }};
            var c = TimelineCompiler.Compile(a,TestAssets.Manifest()).Timeline!;
            Assert.Single(c.OrderedCues.Where(x=>x.Injected));
            Assert.Equal(time+3,c.OrderedCues.Last().T);
        }
    }
    sealed class SessionAdapter : IGameDirectorAdapter, IDirectorSessionAdapter
    {
        public int Begins, Ends, Spawns;
        public double Distance;
        public float Scale=1;
        public bool Throw;
        public void BeginSession(){Begins++;Distance=0;}
        public void EndSession(){Ends++;Scale=1;}
        public void AdvancePresentation(double dt,double t){Distance+=dt*Scale;}
        public void ApplyCameraShot(ShotSpec s){}
        public void PlayAnimation(string r,string c,float f){}
        public void MoveRole(string r,string l,float s,string h){}
        public void FaceRole(string r,string h){}
        public void SpawnRole(string r,string l,string h){Spawns++;if(Throw)throw new Exception("missing prefab");}
        public void DespawnRole(string r){}
        public void PlayAudio(string a,float v){}
        public void StopAudio(string a){}
        public void SetTimeScale(float s){Scale=s;}
        public void Marker(string l){}
    }
    [Fact] public void Clock_integrates_boundaries_and_cleanup_runs_once()
    {
        var a=new TimelineAsset {Id="clock", Cues=new() {
            new Cue {T=1,Type=CueTypes.WorldTimeScale,Scale=.5f,Duration=2},
            new Cue {T=4,Type=CueTypes.Marker,Label="end"}
        }};
        var c=TimelineCompiler.Compile(a,TestAssets.Manifest()).Timeline!;
        foreach(var dt in new[]{.1,4.0}) {
            var adapter=new SessionAdapter(); var p=new TimelinePlayer(c,adapter);p.Play();
            while(p.State==PlayerState.Playing)p.Tick(dt);
            Assert.Equal(3,adapter.Distance,6); Assert.Equal(1,adapter.Ends);
            Assert.Equal(new[]{1.0,3.0,4.0},p.Events.Select(x=>x.Time));
            p.Stop();Assert.Equal(1,adapter.Ends);
        }
    }
    [Fact] public void Adapter_failure_is_terminal_and_cleans_up()
    {
        var a=new SessionAdapter {Throw=true};
        var p=new TimelinePlayer(TimelineCompiler.Compile(TestAssets.ValidTimeline(),TestAssets.Manifest()).Timeline!,a);
        p.Play();p.Tick(3);p.Tick(3);
        Assert.Equal(PlayerState.Failed,p.State);Assert.Equal("missing prefab",p.Failure);
        Assert.Equal(1,a.Spawns);Assert.Equal(1,a.Ends);
    }
}
