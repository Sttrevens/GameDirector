using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;

namespace GameDirector.Decisions.Tests;

/// <summary>Pure task behavior: exact shot-local camera edits, manifest-derived
/// candidate suggestions, evidence eligibility and path scrubbing.</summary>
public class DecisionTaskTests
{
    private static readonly string SourceDir = Path.GetTempPath();
    private readonly CapabilityManifest manifest = TestRig.Manifest(SourceDir);

    [Fact]
    public void CameraApplyChangesExactlyOneCamera()
    {
        var film = TestRig.ValidFilm();
        var replacement = new ShotSpec { Type = "dolly", Subject = "hero", Frame = "closeup", From = "cam_north", To = "loc_gate", Ease = "in" };
        var revised = CameraChoiceTask.Apply(film, "shot-02", replacement);

        // Every other shot is byte-identical: timing, purpose, camera.
        var before = film.Scenes.SelectMany(s => s.Shots).ToDictionary(s => s.Id);
        var after = revised.Scenes.SelectMany(s => s.Shots).ToDictionary(s => s.Id);
        Assert.Equal(before.Keys, after.Keys);
        Assert.Equal(DslJson.Serialize(before["shot-01"]), DslJson.Serialize(after["shot-01"]));
        // The target shot keeps id, timing and purpose; only the camera changed.
        Assert.Equal(before["shot-02"].Start, after["shot-02"].Start);
        Assert.Equal(before["shot-02"].End, after["shot-02"].End);
        Assert.Equal(before["shot-02"].Purpose, after["shot-02"].Purpose);
        Assert.Equal("dolly", after["shot-02"].Camera.Type);
        Assert.Equal("loc_gate", after["shot-02"].Camera.To);
        // Performance, audio and subtitles are untouched.
        Assert.Equal(DslJson.Serialize(film.Scenes.Select(s => s.Performance)), DslJson.Serialize(revised.Scenes.Select(s => s.Performance)));
        Assert.Equal(DslJson.Serialize(film.Audio), DslJson.Serialize(revised.Audio));
        Assert.Equal(DslJson.Serialize(film.Subtitles), DslJson.Serialize(revised.Subtitles));
        Assert.Equal(film.Title, revised.Title);
        // The input film is not mutated.
        Assert.Equal("lockoff", film.Scenes[0].Shots[1].Camera.Type);
    }

    [Fact]
    public void SuggestedCameraCandidatesCompileAndUseManifestVocabulary()
    {
        var film = TestRig.ValidFilm();
        var shot = CameraChoiceTask.FindShot(film, "shot-01");
        var suggestions = CameraChoiceTask.Suggest(manifest, shot);
        Assert.NotEmpty(suggestions);
        Assert.True(suggestions.Count <= CameraChoiceTask.MaxCandidates);
        Assert.Equal(suggestions.Count, suggestions.Select(c => c.Id).Distinct().Count());
        foreach (var candidate in suggestions)
        {
            Assert.Contains(candidate.Camera.Type, manifest.ShotTypes);
            Assert.Contains(candidate.Camera.Frame, manifest.FrameTypes);
            Assert.True(candidate.Camera.From == "current" || manifest.FindLocation(candidate.Camera.From) != null);
            Assert.True(candidate.Camera.To == null || candidate.Camera.To == "current" || manifest.FindLocation(candidate.Camera.To) != null);
            Assert.Null(CameraChoiceTask.ValidateCandidate(film, "shot-01", candidate.Camera, manifest));
        }
        // The enumeration covers the manifest's frame vocabulary with no
        // hardcoded frame preference: medium is not privileged, and a manifest
        // without a medium frame still yields a full bounded subset.
        Assert.Contains(suggestions, c => c.Camera.Frame != "medium" && c.Camera.Type != ShotVocabulary.DefaultShotId);
        Assert.Equal(manifest.ShotTypes.OrderBy(x => x), suggestions.Select(c => c.Camera.Type).Distinct().OrderBy(x => x));
        var noMedium = TestRig.Manifest(SourceDir);
        noMedium.FrameTypes = noMedium.FrameTypes.Where(f => f != "medium").ToList();
        var withoutMedium = CameraChoiceTask.Suggest(noMedium, shot);
        Assert.NotEmpty(withoutMedium);
        Assert.DoesNotContain(withoutMedium, c => c.Camera.Frame == "medium");
        // Deterministic: same manifest and shot produce the same bounded subset.
        Assert.Equal(suggestions.Select(c => c.Id), CameraChoiceTask.Suggest(manifest, shot).Select(c => c.Id));
    }

    [Fact]
    public void UnsupportedCameraCandidateFailsCompilerValidation()
    {
        var film = TestRig.ValidFilm();
        var unknown = new ShotSpec { Type = "helicopter", Subject = "hero", Frame = "full", From = "cam_north" };
        Assert.NotNull(CameraChoiceTask.ValidateCandidate(film, "shot-01", unknown, manifest));
        var unknownRole = new ShotSpec { Type = "lockoff", Subject = "ghost", Frame = "full", From = "cam_north" };
        Assert.NotNull(CameraChoiceTask.ValidateCandidate(film, "shot-01", unknownRole, manifest));
    }

    [Fact]
    public void PerformanceEligibilityRequiresClaimsBackedByEvidence()
    {
        var catalog = TestRig.Catalog("any-hash");
        var eligible = PerformanceMatchTask.Eligible(catalog);
        Assert.Single(eligible);
        Assert.Equal(TestRig.CheerCandidateId, PerformanceMatchTask.CandidateId(eligible[0]));
    }

    [Fact]
    public void CandidateIdsAreUnambiguousWhenNamesContainTheSeparator()
    {
        PerformanceEntry Entry(string actor, string clip) => new() { Actor = actor, Clip = clip };
        var ids = new[] { Entry("a/b", "c"), Entry("a", "b/c"), Entry("a/b", "c/"), Entry("ab", "c") }
            .Select(PerformanceMatchTask.CandidateId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        // The length prefix makes the split point explicit: "8:streamer/Cheer".
        Assert.Equal("8:streamer/Cheer", PerformanceMatchTask.CandidateId(Entry("streamer", "Cheer")));
        Assert.Equal("3:a/b/c", PerformanceMatchTask.CandidateId(Entry("a/b", "c")));
    }

    [Fact]
    public void QuestionBuildersRejectExcessInsteadOfTruncating()
    {
        var entries = Enumerable.Range(0, PerformanceMatchTask.MaxCandidates + 1)
            .Select(i => new PerformanceEntry { Actor = "streamer", Clip = "Take" + i, Meaning = "m" + i,
                Evidence = new List<PerformanceEvidence> { new() { Kind = "design", Source = "n" + i } } }).ToArray();
        // Eligibility returns the full set; the builder refuses the excess.
        Assert.Equal(PerformanceMatchTask.MaxCandidates + 1, entries.Length);
        Assert.Throws<ArgumentException>(() => PerformanceMatchTask.BuildQuestion("intent", entries));
        var sounds = Enumerable.Range(0, AudioMatchTask.MaxCandidates + 1)
            .Select(i => new AudioCandidate(new string((char)('a' + i % 26), 64) + i, "s" + i + ".wav", "described " + i, 1)).ToArray();
        Assert.Throws<ArgumentException>(() => AudioMatchTask.BuildQuestion("intent", "sfx", sounds));
    }

    [Fact]
    public void AudioQuestionsRequireAuthoredDescriptions()
    {
        // A file name is never offered as evidence of how a sound plays.
        Assert.Throws<ArgumentException>(() => AudioMatchTask.BuildQuestion("intent", "sfx",
            new[] { new AudioCandidate(new string('b', 64), "wind.wav", null, 3) }));
        Assert.Throws<ArgumentException>(() => AudioMatchTask.BuildQuestion("intent", "sfx",
            new[] { new AudioCandidate(new string('b', 64), "wind.wav", "  ", 3) }));
        var question = AudioMatchTask.BuildQuestion("intent", "sfx",
            new[] { new AudioCandidate(new string('b', 64), "wind.wav", "cold wind over ice", 3) });
        Assert.Contains("cold wind over ice", question.Candidates[0].Description);
        Assert.Contains("name alone is not evidence", question.Instructions);
    }

    [Fact]
    public void ChoiceQuestionsAlwaysOfferTheNoneExit()
    {
        var catalog = TestRig.Catalog("h");
        var eligible = PerformanceMatchTask.Eligible(catalog);
        var question = PerformanceMatchTask.BuildQuestion("a beat of relief", eligible);
        Assert.Equal(DecisionPolicyBounds.NoneCandidateId, question.Candidates.Last().Id);

        var shot = CameraChoiceTask.FindShot(TestRig.ValidFilm(), "shot-01");
        var camera = CameraChoiceTask.BuildQuestion(shot, "lonely", CameraChoiceTask.Suggest(manifest, shot), manifest);
        Assert.Equal(DecisionPolicyBounds.NoneCandidateId, camera.Candidates.Last().Id);

        var audio = AudioMatchTask.BuildQuestion("tension", "sfx", new[] { new AudioCandidate(new string('b', 64), "wind.wav", "cold wind", 3) });
        Assert.Equal(DecisionPolicyBounds.NoneCandidateId, audio.Candidates.Last().Id);
    }

    [Fact]
    public void ScrubberRedactsLocalPaths()
    {
        var root = Path.Combine(SourceDir, "gd-secret-store");
        var text = $@"brief referencing {root}\jobs\1.json and C:\other\file.txt and \\nas\share\x and /home/u/key";
        var scrubbed = LocalPathScrubber.Scrub(text, root);
        Assert.Null(LocalPathScrubber.FindPathLike(scrubbed));
        Assert.DoesNotContain("gd-secret-store", scrubbed);
        Assert.Equal(@"C:\proj", LocalPathScrubber.FindPathLike(@"open C:\proj now"));
    }

    [Fact]
    public void PathGuardReadsThroughJsonEscapingWithoutFalsePositives()
    {
        // In serialized JSON one real backslash travels as two. A genuine path
        // is still caught after unescaping…
        var realPayload = System.Text.Json.JsonSerializer.Serialize(new { note = @"open C:\proj\secret.txt and \\nas\share" });
        Assert.NotNull(LocalPathScrubber.FindPathLike(realPayload));
        // …while a scrubbed single-backslash remnant like "[local-path]\takes\x"
        // is not a UNC path and must not trip the guard.
        var scrubbedPayload = System.Text.Json.JsonSerializer.Serialize(new { note = @"[local-path]\takes\scout.mp4" });
        Assert.Null(LocalPathScrubber.FindPathLike(scrubbedPayload));
    }

    [Fact]
    public void CandidateDescriptionsCarryNoPaths()
    {
        var catalog = TestRig.Catalog("h");
        catalog.Performances[0].Evidence[0].Source = $@"{SourceDir}takes\scout.mp4";
        var question = PerformanceMatchTask.BuildQuestion("intent", PerformanceMatchTask.Eligible(catalog), SourceDir);
        var payload = JevWire.SerializeRequest(JevWire.BuildRequest(new DecisionRequest("m", new Dictionary<string, string>(), new[] { question })));
        Assert.Null(LocalPathScrubber.FindPathLike(payload));
    }

    [Fact]
    public void FilmRankingRanksByScoreThenConfidenceDeterministically()
    {
        var candidates = new[]
        {
            new FilmRankingCandidate { Id = "c1", Film = TestRig.ValidFilm() },
            new FilmRankingCandidate { Id = "c2", Film = TestRig.ValidFilm() },
        };
        var answers = new[]
        {
            new DecisionAnswer("rank-c1", DecisionQuestionKind.Score, null, 0.9, null, 1.0, new Dictionary<string, double>()),
            new DecisionAnswer("rank-c2", DecisionQuestionKind.Score, null, 0.5, null, 2.0, new Dictionary<string, double>()),
        };
        var ranked = FilmRankingTask.Rank(answers, candidates, new Dictionary<string, string>());
        Assert.Equal("c2", ranked[0].CandidateId); // higher score wins despite lower confidence
        Assert.Equal("c1", ranked[1].CandidateId);
        // Candidates without answers (ineligible) sort last.
        var withMissing = FilmRankingTask.Rank(Array.Empty<DecisionAnswer>(), candidates, new Dictionary<string, string> { ["c1"] = "does not compile" });
        Assert.Equal("does not compile", withMissing[0].Evidence);
    }

    [Fact]
    public void SemanticReviewChecksBecomeNoulAndScoreQuestions()
    {
        var noul = SemanticReviewTask.BuildCheck("arc", "noul", "The outline resolves the arc.", null, "shot-01 does things", "outline");
        Assert.Equal(DecisionQuestionKind.Noul, noul.Kind);
        Assert.Equal(new[] { "false", "true" }, noul.Candidates.Select(c => c.Id).OrderBy(x => x).ToArray());
        Assert.Contains("The outline resolves the arc.", noul.Instructions);
        Assert.Contains("shot-01 does things", noul.Instructions);
        Assert.DoesNotContain("rendered footage", noul.Candidates[0].Description);

        var score = SemanticReviewTask.BuildCheck("pace", "score", "Pacing?", new[] { "dragging", "steady", "tight" }, "text", null);
        Assert.Equal(DecisionQuestionKind.Score, score.Kind);
        Assert.Equal(3, score.Candidates.Count);
        Assert.Equal("dragging", score.Candidates[0].Description); // index 0 = first legend label
        // The legend travels as the criteria array on the wire.
        var wire = JevWire.BuildRequest(new DecisionRequest("m", new Dictionary<string, string>(), new[] { score }));
        var criteria = wire["questions"]!["pace"]!["criteria"] as System.Text.Json.Nodes.JsonArray;
        Assert.NotNull(criteria);
        Assert.Equal("tight", criteria![2]!.GetValue<string>());

        Assert.Throws<ArgumentException>(() => SemanticReviewTask.BuildCheck("bad", "ranking", "?", null, "text", null));
        Assert.Throws<ArgumentException>(() => SemanticReviewTask.BuildCheck("bad", "score", "?", new[] { "only" }, "text", null));
    }
}
