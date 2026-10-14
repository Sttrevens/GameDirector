using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

public class FilmPlanTests : TempPaths
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
    [Fact] public void CacheKeyIncludesTheEncodingProfile()
    {
        var film = Film(); var manifest = Manifest();
        var keyed = FilmCompiler.Prepare(film, manifest).Shots.Select(s => s.CacheKey).ToArray();
        Assert.Equal(keyed, FilmCompiler.Prepare(film, manifest, MediaProfile.Default).Shots.Select(s => s.CacheKey).ToArray());
        foreach (var profile in new[] { MediaProfile.Default with { Crf = 23 }, MediaProfile.Default with { Preset = "slow" },
            MediaProfile.Default with { PixelFormat = "yuv444p" }, MediaProfile.Default with { VideoCodec = "libx265" } })
            Assert.NotEqual(keyed, FilmCompiler.Prepare(film, manifest, profile).Shots.Select(s => s.CacheKey).ToArray());
    }
    [Fact] public void EveryPreparedShotMustPassTakeAdmission()
    {
        // Capability gates are take admission, not engine capture: a cut-only
        // adapter rejects the default 0.2 fade at Prepare, before any frame.
        var m = Manifest(); m.Capabilities["actor.animation.fade"] = "cut-only";
        var unsupported = Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(Film(), m));
        Assert.Contains("fade: 0", unsupported.Message);
        var f = Film(); foreach (var cue in f.Scenes[0].Performance) cue.Fade = 0;
        Assert.Equal(2, FilmCompiler.Prepare(f, m).Shots.Count);
        // A camera param typo fails validation the same way, not silently in the engine.
        f.Scenes[0].Shots[0].Camera.Params = new() { ["tiltDeg"] = 5 };
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(f, m));
    }
    [Fact] public void EmptyPreRollNeedsNoCameraOrSubstituteSubject()
    {
        var m = Manifest(); m.Roles[0].PresentAtStart = false;
        var f = Film(); f.Scenes[0].Shots.RemoveAt(0); f.Scenes[0].Performance.Clear();
        f.Scenes[0].Performance.Add(new Cue { Type = "marker", T = .5, Label = "replayed" });
        f.Scenes[0].Performance.Add(new Cue { Type = "actor.spawn", T = 1, Role = "hero", Location = "front" });
        var shot = FilmCompiler.Prepare(f, m).Shots.Single();
        var camera = Assert.Single(shot.Timeline.Cues.Where(c => c.Type == "camera.shot"));
        Assert.Equal(1, camera.T); Assert.Equal("hero", camera.Shot.Subject);
        Assert.Contains(shot.Timeline.Cues, c => c.Label == "replayed" && c.T == .5);
        Assert.Equal(24, shot.CaptureFromFrame);
        Assert.Equal(2, shot.Timeline.Cues.Single(c => c.Label == "shot-end").T);
    }
    [Fact] public void CompileDiagnosticsAreRewrittenToFilmPlanCoordinates()
    {
        // The text fed back to the LLM names the field in the document it wrote,
        // never an index into a synthetic per-shot timeline.
        var film = Film(); film.Scenes[0].Performance[0].Clip = "nosuchclip";
        var ex = Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
        Assert.Contains("scenes[0].performance[0].clip", ex.Message);
        Assert.Contains("nosuchclip", ex.Message);
        Assert.DoesNotContain("cue[", ex.Message);
        film = Film(); film.Scenes[0].Shots[1].Camera.From = "nowhere";
        ex = Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
        Assert.Contains("scenes[0].shots[1].camera.from: unknown location 'nowhere'", ex.Message);
    }
    [Fact] public void PreRollDoesNotRequireLockoffCapability()
    {
        var m = Manifest(); m.ShotTypes.Remove("lockoff");
        var film = Film(); foreach (var s in film.Scenes[0].Shots) s.Camera.Type = "orbit";
        var prepared = FilmCompiler.Prepare(film, m);
        Assert.All(prepared.Shots, s => Assert.Equal("orbit", Assert.Single(s.Timeline.Cues.Where(c => c.Type == "camera.shot")).Shot.Type));
        Assert.Equal(0, prepared.Shots[0].CaptureFromFrame); Assert.Equal(24, prepared.Shots[1].CaptureFromFrame);
    }
    [Theory] [InlineData(24)] [InlineData(25)] [InlineData(30)] [InlineData(60)]
    public void ValidatedShotBoundariesAreSnappedBeforeCuesAndCacheIdentity(int fps)
    {
        var film = Film(); film.FrameRate = fps;
        var exact = FilmCompiler.Prepare(film, Manifest());
        film.Scenes[0].Shots[1].Start += .000005 / fps; film.Scenes[0].Shots[1].End -= .000005 / fps;
        var original = DslJson.Serialize(film); var prepared = FilmCompiler.Prepare(film, Manifest());
        var shot = prepared.Shots[1];
        Assert.Equal(1, shot.Start); Assert.Equal(2, shot.End); Assert.Equal(fps, shot.CaptureFromFrame);
        Assert.Equal(1, prepared.Plan.Scenes[0].Shots[1].Start); Assert.Equal(2, prepared.Plan.Scenes[0].Shots[1].End);
        Assert.Equal(1, shot.Timeline.Cues.Single(c => c.Type == "camera.shot" && c.T > 0).T);
        Assert.Equal(2, shot.Timeline.Cues.Single(c => c.Label == "shot-end").T);
        Assert.Equal(1, shot.Timeline.Cues.Single(c => c.Type == "camera.shot" && c.T > 0).Shot.DurationSeconds);
        Assert.Equal(exact.Shots[1].CacheKey, shot.CacheKey); Assert.Equal(original, DslJson.Serialize(film));
        film.Scenes[0].Shots[1].Start = 1 + .00002 / fps;
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
    }
    [Fact] public void AlignmentNoiseCannotCreateAnEmptyShot()
    {
        var film = Film(); film.Scenes[0].Shots[1].Start = 1 + .000002 / 24; film.Scenes[0].Shots[1].End = 1 + .000004 / 24;
        Assert.Throws<ArgumentException>(() => FilmCompiler.Prepare(film, Manifest()));
    }
    [Fact] public void ReadRejectsMisspelledAndMissingFieldsWithTheirJsonPaths()
    {
        var path = TempPath("gd-film-", ".json");
        try
        {
            File.WriteAllText(path, DslJson.Serialize(Film()).Replace("\"purpose\":", "\"purpouse\":"));
            Assert.Contains("scenes[0].shots[0].purpouse: unknown property",
                Assert.Throws<AuthoredInputException>(() => FilmCompiler.Read(path)).Message);
            File.WriteAllText(path, "{\"title\":\"x\"}");
            Assert.Contains("version: required", Assert.Throws<AuthoredInputException>(() => FilmCompiler.Read(path)).Message);
        }
        finally { File.Delete(path); }
    }
}
