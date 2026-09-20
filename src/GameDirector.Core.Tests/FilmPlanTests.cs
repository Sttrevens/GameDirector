using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

public class FilmPlanTests
{
    private static CapabilityManifest Manifest() => new() { Game = "film-test", Roles = new() { new() { Id = "hero", DefaultActor = "body", PresentAtStart = true } },
        Actors = new() { new() { Id = "body", Clips = new() { "wave" } } }, Locations = new() { new() { Id = "front", Position = new float[] { 0, 1, 3 } } },
        ShotTypes = new() { "lockoff", "orbit" }, FrameTypes = new() { "full", "closeup" },
        Capabilities = new() { ["director.mode"] = "offline-sandbox", ["presentation.sourceFingerprint"] = "v1" } };
    private static FilmPlan Film()
    {
        var f = FilmCompiler.Starter(Manifest());
        f.Scenes[0].Performance.Add(new Cue { Type = "actor.anim", T = 0, Role = "hero", Clip = "wave" });
        f.Scenes[0].Shots.Add(new FilmShot { Id = "reaction", Start = 1, End = 2, Purpose = "Hold the reaction", Camera = new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "closeup", From = "front" } });
        return f;
    }
    [Fact] public void SoundAndCaptionRevisionsRetainPictureCacheAndEnforceFinalTime()
    {
        var f = Film(); var before = FilmCompiler.Prepare(f, Manifest());
        f.Audio.Add(new FilmAudioCue { Id = "voice", MediaId = new string('a',64), Bus = "dialogue", At = .5, Duration = 1, FadeIn = .1, FadeOut = .1 });
        f.Subtitles.Add(new FilmSubtitle { Start = .5, End = 1.5, Text = "Hello" });
        Assert.Equal(before.Shots.Select(s => s.CacheKey), FilmCompiler.Prepare(f, Manifest()).Shots.Select(s => s.CacheKey));
        f.Audio[0].At = 5; Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(f, Manifest()));
        f.Audio[0].At = .5; f.Subtitles[0].End = 50; Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(f, Manifest()));
    }
    [Fact] public void ChangingOnlyOneCameraInvalidatesOnlyItsPixels()
    {
        var film = Film(); var before = FilmCompiler.Prepare(film, Manifest());
        film.Scenes[0].Shots[1].Camera.Type = "orbit";
        var after = FilmCompiler.Prepare(film, Manifest());
        Assert.Equal(before.Shots[0].CacheKey, after.Shots[0].CacheKey);
        Assert.NotEqual(before.Shots[1].CacheKey, after.Shots[1].CacheKey);
        Assert.Contains(after.Shots[1].Timeline.Cues, c => c.Type == "actor.anim" && c.T == 0);
        Assert.Contains(after.Shots[1].Timeline.Cues, c => c.Type == "camera.shot" && c.T == 1);
        Assert.Equal(1, after.Shots[1].Start);
    }
    [Fact] public void PerformanceOrSourceChangesInvalidateAllAffectedCoverage()
    {
        var film = Film(); var manifest = Manifest(); var before = FilmCompiler.Prepare(film, manifest);
        film.Scenes[0].Performance[0].Fade = .4f;
        var after = FilmCompiler.Prepare(film, manifest);
        Assert.All(before.Shots.Select((s, i) => s.CacheKey != after.Shots[i].CacheKey), Assert.True);
        manifest.Capabilities["presentation.sourceFingerprint"] = "v2";
        var changedSource = FilmCompiler.Prepare(film, manifest);
        Assert.All(after.Shots.Select((s, i) => s.CacheKey != changedSource.Shots[i].CacheKey), Assert.True);
    }
    [Fact] public void EditorialTextDoesNotInvalidatePixelsAndCompilationDoesNotMutateInput()
    {
        var film = Film(); var original = DslJson.Serialize(film); var before = FilmCompiler.Prepare(film, Manifest());
        Assert.Equal(original, DslJson.Serialize(film));
        film.Title = "New title"; film.Scenes[0].Shots[0].Purpose = "Clearer intent";
        Assert.Equal(before.Shots[0].CacheKey, FilmCompiler.Prepare(film, Manifest()).Shots[0].CacheKey);
    }
    [Fact] public void UnsupportedAudioAndUnknownBindingsAreRejectedBeforeCapture()
    {
        var film = Film(); film.Scenes[0].Performance[0].Clip = "nonexistent";
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
        film = Film(); film.Scenes[0].Performance.Add(new Cue { Type = "audio.play", AudioId = "voice" });
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
        film = Film(); film.Scenes[0].Shots[1].Id = film.Scenes[0].Shots[0].Id;
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
    }
    [Fact] public void PreRollUsesAnAvailableActorWhenShotSubjectSpawnsLater()
    {
        var m = Manifest(); m.Roles.Add(new RoleDescriptor { Id = "late", DefaultActor = "body", PresentAtStart = false });
        var f = Film(); f.Scenes[0].Shots.RemoveAt(0);
        f.Scenes[0].Performance.Add(new Cue { Type = "actor.spawn", T = 1, Role = "late", Location = "front" });
        f.Scenes[0].Shots[0].Camera.Subject = "late";
        var shot = FilmCompiler.Prepare(f, m).Shots.Single();
        Assert.Equal("hero", shot.Timeline.Cues.Single(c => c.Type == "camera.shot" && c.T == 0).Shot.Subject);
        Assert.Equal("late", shot.Timeline.Cues.Single(c => c.Type == "camera.shot" && c.T == 1).Shot.Subject);
        f.Scenes[0].Performance.Insert(0, new Cue { Type = "actor.despawn", T = 0, Role = "hero" });
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(f, m));
    }
}
