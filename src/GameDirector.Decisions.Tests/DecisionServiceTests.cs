using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

/// <summary>Service workflows: catalog freshness gates, no-candidate honesty,
/// shot-local camera proposals, dedupe/conflict, policy disabling, uncalibrated
/// defaults, bounded candidate sets, provider failure and cancellation.
/// Policies ship uncalibrated; tests that expect a selected outcome configure
/// an explicit per-task threshold. Production code paths never see a provider.</summary>
public class DecisionServiceTests
{
    [Fact]
    public async Task MissingCatalogIsNoMatchWithoutProviderCall()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-1", "a beat of relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NoMatch, record.Outcome);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("none", record.ProviderId); // configured but never called
        Assert.Equal(PerformanceMatchTask.QuestionVersion, record.QuestionVersion);
        Assert.True(rig.RecordExists(DecisionTasks.PerformanceMatch, "req-1"));
    }

    [Fact]
    public async Task StaleCatalogIsNeedsReviewAndNeverCallsProvider()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.PerformanceMatch.MinConfidence = 0.5);
        // Catalog pinned to a different manifest identity: evidence is stale.
        rig.WriteCatalog(TestRig.Catalog(new string('0', 64)));
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-2", "a beat of relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("not current", record.Reason);
        Assert.Equal(0, provider.Calls);
        // The generation preparation hook refuses stale meanings as well:
        // the outcome is audited (dedupeable) and no evidence is produced.
        var prep = await rig.Decisions.PreparePerformanceEvidence("proj", "a beat of relief", CancellationToken.None);
        Assert.Null(prep.EvidenceContext);
        Assert.Equal(DecisionOutcomes.NeedsReview, prep.DecisionOutcome);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task FreshCatalogSelectsAndAudits()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("performance", TestRig.CheerCandidateId, 0.92) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.PerformanceMatch.MinConfidence = 0.75); // explicit per-task bar; no invented default
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-3", "a beat of relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.Selected, record.Outcome);
        Assert.Equal(TestRig.CheerCandidateId, record.SelectedCandidateId);
        Assert.Equal(1, provider.Calls);
        // Audit fields: manifest + evidence hashes, candidates, model, versions.
        Assert.Equal(rig.ManifestHash, record.ManifestHash);
        Assert.Equal(FilmCompiler.Hash(TestRig.Catalog(rig.ManifestHash)), record.EvidenceHash);
        Assert.Contains(TestRig.CheerCandidateId, record.CandidateIds);
        Assert.Equal("typesafe/jev-1.13", record.Model);
        Assert.Equal(ServiceRig.Settings.Endpoint, record.ProviderEndpoint);
        Assert.Single(record.Answers);
        Assert.Equal(0.92, record.Answers[0].Confidence);
        Assert.Equal(0.75, record.MinConfidence); // per-task threshold, not a global default
    }

    [Fact]
    public async Task UncalibratedPolicyNeverAutoSelectsAndRecordsNullThreshold()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("performance", TestRig.CheerCandidateId, 0.99) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        // Default policies: enabled but uncalibrated (MinConfidence null).
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-3u", "a beat of relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("no calibrated confidence threshold", record.Reason);
        Assert.Null(record.MinConfidence); // blank is a real state in the audit, not zero
        Assert.Null(record.SelectedCandidateId);
        Assert.Single(record.Answers); // the answer is attached for human review
        Assert.Equal(0.99, record.Answers[0].Confidence);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task KeyRotationKeepsIdentityButEndpointOrPolicyChangeConflicts()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("camera", "cand-wide", 0.9) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var request = new CameraChoiceRequest("req-idem", "shot-01", "wider", null, null, TestRig.ValidFilm(), candidates);
        await rig.Decisions.CameraChoice("proj", request, CancellationToken.None);
        Assert.Equal(1, provider.Calls);

        // API key rotation alone must not orphan the audit record: the key is a
        // memory-only secret and never part of decision identity.
        rig.Decisions.ConfigureProvider(ServiceRig.SettingsRotatedKey);
        var replay = await rig.Decisions.CameraChoice("proj", request, CancellationToken.None);
        Assert.Equal(DecisionOutcomes.Selected, replay.Outcome);
        Assert.Equal(1, provider.Calls);

        // The endpoint identifies the answering deployment: changing it (or the
        // policy) with a reused requestId conflicts instead of silently mixing.
        rig.Decisions.ConfigureProvider(ServiceRig.SettingsOtherEndpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Decisions.CameraChoice("proj", request, CancellationToken.None));
        rig.Decisions.ConfigureProvider(ServiceRig.SettingsRotatedKey);
        rig.Calibrate(p => p.CameraChoice.Enabled = false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Decisions.CameraChoice("proj", request, CancellationToken.None));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task DisabledTaskNeverCallsProvider()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("performance", TestRig.CheerCandidateId, 0.99) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        rig.Calibrate(p => { p.PerformanceMatch.Enabled = false; p.PerformanceMatch.MinConfidence = 0.5; });
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-4", "relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("disabled", record.Reason);
        Assert.Equal("none", record.ProviderId);
        Assert.Equal(0, provider.Calls);
        // Disabled matching also feeds nothing into generation preparation.
        var prep = await rig.Decisions.PreparePerformanceEvidence("proj", "relief", CancellationToken.None);
        Assert.Same(PerformanceEvidencePreparation.None, prep);
    }

    [Fact]
    public async Task UnconfiguredProviderIsNeedsReview()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        rig.WriteCatalog(TestRig.Catalog(rig.ManifestHash));
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-5", "relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("not configured", record.Reason);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ExcessEligibleCatalogIsReviewedNotTruncated()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.PerformanceMatch.MinConfidence = 0.5);
        // 25 evidence-backed entries exceed the bounded 24-candidate question.
        var catalog = new PerformanceCatalog
        {
            ManifestSha256 = rig.ManifestHash,
            Performances = Enumerable.Range(1, PerformanceMatchTask.MaxCandidates + 1).Select(i => new PerformanceEntry
            {
                Actor = "streamer", Clip = "Take" + i.ToString("00"), Meaning = "rehearsed beat " + i,
                Evidence = new List<PerformanceEvidence> { new() { Kind = "design", Source = "rehearsal note " + i } },
            }).ToList(),
        };
        rig.WriteCatalog(catalog);
        var eligible = PerformanceMatchTask.Eligible(catalog);
        Assert.Equal(PerformanceMatchTask.MaxCandidates + 1, eligible.Count);
        var record = await rig.Decisions.PerformanceMatch("proj", new PerformanceMatchRequest("req-excess", "relief"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("exceed", record.Reason);
        Assert.Contains("nothing was offered", record.Reason);
        Assert.Equal(0, provider.Calls); // no silent exclusion of candidate 24+
        Assert.Equal(eligible.Count, record.CandidateIds.Length); // audit = full evidence base, explicitly not offered
    }

    [Fact]
    public async Task CameraChoiceProposalIsShotLocalAndDocumentsUntouched()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("camera", "cand-close", 0.91) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var film = TestRig.ValidFilm();
        var candidates = new List<CameraCandidateInput>
        {
            new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }),
            new("cand-close", new ShotSpec { Type = "orbit", Subject = "hero", Frame = "closeup", From = "cam_north" }),
        };
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-6", "shot-02", "move closer", null, null, film, candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.Selected, record.Outcome);
        var proposal = record.Proposal!;
        Assert.NotNull(proposal);
        // Exactly the chosen camera on exactly the requested shot.
        Assert.Equal("orbit", proposal.Scenes[0].Shots[1].Camera.Type);
        Assert.Equal("closeup", proposal.Scenes[0].Shots[1].Camera.Frame);
        Assert.Equal(DslJson.Serialize(film.Scenes[0].Shots[0]), DslJson.Serialize(proposal.Scenes[0].Shots[0]));
        Assert.Equal(DslJson.Serialize(film.Audio), DslJson.Serialize(proposal.Audio));
        Assert.Equal(DslJson.Serialize(film.Subtitles), DslJson.Serialize(proposal.Subtitles));
        Assert.Equal(DslJson.Serialize(film.Scenes[0].Performance), DslJson.Serialize(proposal.Scenes[0].Performance));
        // The proposal was not committed over any accepted document.
        Assert.Equal(0, rig.Studio.Documents.Read("proj", "default").Revision);
        Assert.Equal(FilmCompiler.Hash(film), record.FilmHash);
        Assert.Equal(2, record.CandidateIds.Length);
    }

    [Fact]
    public async Task CameraChoiceWithoutCandidatesIsNoMatch()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        var film = TestRig.ValidFilm();
        // A single uncompilable candidate: unknown shot type never reaches the model.
        var candidates = new List<CameraCandidateInput> { new("bad", new ShotSpec { Type = "helicopter", Subject = "hero", Frame = "full", From = "cam_north" }) };
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-7", "shot-01", "anything", null, null, film, candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NoMatch, record.Outcome);
        Assert.Contains("no camera candidate", record.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(record.Proposal);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task UnknownOrMalformedSelectionPreservesOriginalFilm()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("camera", "never-offered", 0.99) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var film = TestRig.ValidFilm();
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        // single candidate + none = a valid choice set
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-8", "shot-01", "anything", null, null, film, candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("unknown or malformed", record.Reason);
        Assert.Null(record.Proposal); // original preserved, review status recorded
    }

    [Fact]
    public async Task LowConfidenceIsNeedsReview()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("camera", "cand-wide", 0.3) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-9", "shot-01", "anything", null, null, TestRig.ValidFilm(), candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Null(record.Proposal);
        Assert.Contains("threshold", record.Reason);
    }

    [Fact]
    public async Task NoneSelectionIsNoMatch()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("camera", DecisionPolicyBounds.NoneCandidateId, 0.97) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-10", "shot-01", "nothing fits", null, null, TestRig.ValidFilm(), candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NoMatch, record.Outcome);
        Assert.Null(record.Proposal);
    }

    [Fact]
    public async Task SameRequestIdIsIdempotentAndChangedInputsConflict()
    {
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("camera", "cand-wide", 0.9) });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var request = new CameraChoiceRequest("req-11", "shot-01", "wider", null, null, TestRig.ValidFilm(), candidates);
        var first = await rig.Decisions.CameraChoice("proj", request, CancellationToken.None);
        var second = await rig.Decisions.CameraChoice("proj", request, CancellationToken.None);
        Assert.Equal(1, provider.Calls); // dedupe: the provider was not re-asked
        Assert.Equal(first.InputHash, second.InputHash);
        Assert.Equal(first.Outcome, second.Outcome);
        var changed = request with { Intent = "closer" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Decisions.CameraChoice("proj", changed, CancellationToken.None));
    }

    [Fact]
    public async Task ProviderFailureRecordsReviewAndPreservesOriginal()
    {
        var provider = new TestRig.FakeProvider((_, _) => throw new DecisionProviderException("HTTP 502"));
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-12", "shot-01", "wider", null, null, TestRig.ValidFilm(), candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("provider failure", record.Reason);
        Assert.Null(record.Proposal);
        Assert.True(rig.RecordExists(DecisionTasks.CameraChoice, "req-12"));
    }

    [Fact]
    public async Task ProviderFailureReasonsCarryNoLocalPaths()
    {
        // Provider error text is scrubbed before it lands in the audit record.
        var failing = new TestRig.FakeProvider((_, _) => throw new DecisionProviderException(@"refusing payload near C:\Users\me\secret\key.txt"));
        await using var rig = await ServiceRig.Start(failing);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-12p", "shot-01", "wider", null, null, TestRig.ValidFilm(), candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Null(LocalPathScrubber.FindPathLike(record.Reason));
        Assert.DoesNotContain("secret", record.Reason);
    }

    [Fact]
    public async Task RequestProtocolValidationIsRecordedAsNeedsReview()
    {
        // The provider path runs the real wire builder; an over-long assembled
        // instruction fails protocol validation inside the ask and must be
        // audited as needs-review, not surface as an unhandled caller error.
        var provider = new TestRig.FakeProvider((req, _) => { JevWire.BuildRequest(req); throw new UnreachableException("validation must stop the ask"); });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.CameraChoice.MinConfidence = 0.5);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        var intent = new string('x', DecisionPolicyBounds.MaxIntentChars); // template + intent exceeds the instruction bound
        var record = await rig.Decisions.CameraChoice("proj", new CameraChoiceRequest("req-12v", "shot-01", intent, null, null, TestRig.ValidFilm(), candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("request protocol validation", record.Reason);
        Assert.Null(record.Proposal);
        Assert.True(rig.RecordExists(DecisionTasks.CameraChoice, "req-12v"));
    }

    [Fact]
    public async Task CancellationRecordsUncertaintyAndReplayDoesNotCallAgain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestRig.FakeProvider(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        var candidates = new List<CameraCandidateInput> { new("cand-wide", new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "wide", From = "cam_north" }) };
        using var cts = new CancellationTokenSource();
        var request = new CameraChoiceRequest("req-13", "shot-01", "wider", null, null, TestRig.ValidFilm(), candidates);
        var pending = rig.Decisions.CameraChoice("proj", request, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(rig.RecordExists(DecisionTasks.CameraChoice, "req-13"));
        var replay = await rig.Decisions.CameraChoice("proj", request, CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, replay.Outcome);
        Assert.Contains("cancelled", replay.Reason);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task AudioMatchUsesOnlyDescribedMedia()
    {
        await using var rig = await ServiceRig.Start(null);
        Assert.Empty(MediaLibrary.List(rig.Studio, "proj"));
        var empty = await rig.Decisions.AudioMatch("proj", new AudioMatchRequest("req-14", "cold wind", "sfx"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NoMatch, empty.Outcome); // empty library: honest no-match

        // Undescribed sounds are never offered: a file name is not evidence.
        rig.WriteMedia("mystery.wav", null);
        var undescribed = await rig.Decisions.AudioMatch("proj", new AudioMatchRequest("req-14b", "cold wind", "sfx"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NoMatch, undescribed.Outcome);
        Assert.Contains("never matched on file names alone", undescribed.Reason);
        Assert.Empty(undescribed.CandidateIds);

        var wind = rig.WriteMedia("wind.wav", "cold mountain wind");
        var drum = rig.WriteMedia("drum.wav", "single taiko hit");
        var provider = new TestRig.FakeProvider(new[] { TestRig.ChoiceAnswer("audio", wind.Id, 0.88) });
        rig.Decisions = new DecisionService(rig.Studio, _ => provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.AudioMatch.MinConfidence = 0.5);
        var record = await rig.Decisions.AudioMatch("proj", new AudioMatchRequest("req-15", "cold wind under the scene", "sfx"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.Selected, record.Outcome);
        Assert.Equal(wind.Id, record.SelectedCandidateId);
        // Audit lists exactly the offered set — the undescribed file is absent.
        Assert.Equal(2, record.CandidateIds.Length);
        Assert.DoesNotContain(record.CandidateIds, id => !new[] { wind.Id, drum.Id }.Contains(id));
        // The provider saw descriptions, not local file paths.
        var payload = JevWire.SerializeRequest(JevWire.BuildRequest(provider.LastRequest!));
        Assert.Null(LocalPathScrubber.FindPathLike(payload));
        Assert.Contains("cold mountain wind", payload);
        Assert.DoesNotContain("mystery.wav", payload);
    }

    [Fact]
    public async Task AudioMatchRejectsExcessInsteadOfTruncating()
    {
        await using var rig = await ServiceRig.Start(null);
        for (var i = 0; i < AudioMatchTask.MaxCandidates + 1; i++)
            rig.WriteMedia("sound-" + i.ToString("00") + ".wav", "described sound number " + i);
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        rig.Decisions = new DecisionService(rig.Studio, _ => provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.AudioMatch.MinConfidence = 0.5);
        var record = await rig.Decisions.AudioMatch("proj", new AudioMatchRequest("req-15x", "wind", "sfx"), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("exceed", record.Reason);
        Assert.Contains("nothing was offered", record.Reason);
        Assert.Equal(0, provider.Calls); // no silent truncation of candidate 24+
        Assert.Equal(AudioMatchTask.MaxCandidates + 1, record.CandidateIds.Length);
    }

    [Fact]
    public async Task ScaffoldPreservesExistingCatalogAndNeverOverwrites()
    {
        await using var rig = await ServiceRig.Start(null);
        // First scaffold creates a skeleton pinned to the live manifest.
        var created = await rig.Decisions.ScaffoldCatalog("proj", CancellationToken.None);
        Assert.True((bool)created.GetType().GetProperty("scaffolded")!.GetValue(created)!);
        var path = Path.Combine(rig.CatalogDir(), "performance-catalog.json");
        var skeleton = DslJson.Load<PerformanceCatalog>(path);
        Assert.Equal(rig.ManifestHash, skeleton.ManifestSha256);
        Assert.Equal(30, skeleton.Performances.Count); // every manifest actor/clip pair

        // Curated content is never overwritten by a later scaffold.
        skeleton.Performances[0].Meaning = "curated meaning";
        skeleton.Performances[0].Evidence.Add(new PerformanceEvidence { Kind = "design", Source = "director note" });
        rig.WriteCatalog(skeleton);
        var again = await rig.Decisions.ScaffoldCatalog("proj", CancellationToken.None);
        Assert.False((bool)again.GetType().GetProperty("scaffolded")!.GetValue(again)!);
        var preserved = DslJson.Load<PerformanceCatalog>(path);
        Assert.Equal("curated meaning", preserved.Performances[0].Meaning);
        Assert.Single(preserved.Performances[0].Evidence);
    }

    [Fact]
    public async Task FilmRankingRanksCandidatesAndRecordsEvidence()
    {
        var provider = new TestRig.FakeProvider(new[]
        {
            new DecisionAnswer("rank-good", DecisionQuestionKind.Score, null, 0.86, null, 3.0, new Dictionary<string, double>()),
            new DecisionAnswer("rank-alt", DecisionQuestionKind.Score, null, 0.7, null, 1.0, new Dictionary<string, double>()),
        });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.FilmRanking.MinConfidence = 0.5);
        var broken = TestRig.ValidFilm();
        broken.Scenes[0].Shots[0].Camera.Type = "helicopter";
        var candidates = new List<FilmCandidateInput>
        {
            new("good", "follows the brief", TestRig.ValidFilm()),
            new("alt", null, TestRig.ValidFilm()),
            new("broken", "uncompilable", broken),
        };
        var record = await rig.Decisions.FilmRanking("proj", new FilmRankingRequest("req-16", "a quiet character study", candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.Selected, record.Outcome);
        Assert.Equal("good", record.SelectedCandidateId);
        // The non-compiling candidate was never offered to the model.
        Assert.Equal(2, provider.LastRequest!.Questions.Count);
        Assert.Equal(3, record.Ranking.Length);
        Assert.Contains(record.Ranking, r => r.CandidateId == "broken" && r.Score == null && r.Evidence.Contains("does not compile"));
        Assert.Contains("not a visual judgment", record.Reason); // honesty note
    }

    [Fact]
    public async Task FilmRankingUncalibratedKeepsRankingForHumanReview()
    {
        var provider = new TestRig.FakeProvider(new[]
        {
            new DecisionAnswer("rank-good", DecisionQuestionKind.Score, null, 0.99, null, 3.0, new Dictionary<string, double>()),
        });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        // Default policy: uncalibrated.
        var candidates = new List<FilmCandidateInput> { new("good", null, TestRig.ValidFilm()) };
        var record = await rig.Decisions.FilmRanking("proj", new FilmRankingRequest("req-16u", "a quiet character study", candidates), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, record.Outcome);
        Assert.Contains("no calibrated confidence threshold", record.Reason);
        Assert.Null(record.SelectedCandidateId);
        Assert.Single(record.Ranking); // the ranking itself is recorded
        Assert.Null(record.MinConfidence);
    }

    [Fact]
    public async Task SemanticReviewCompletesWithAnswersAndNeverSelects()
    {
        var provider = new TestRig.FakeProvider(new[]
        {
            new DecisionAnswer("arc", DecisionQuestionKind.Noul, null, null, 0.91, null, new Dictionary<string, double>()),
            new DecisionAnswer("pace", DecisionQuestionKind.Score, null, 0.66, null, 1.8, new Dictionary<string, double>()),
        });
        await using var rig = await ServiceRig.Start(provider);
        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        // Default policy: enabled, uncalibrated — semantic review has no
        // auto-selection at all, so a threshold is never needed.
        var request = new SemanticReviewRequest("req-17", "shot-01 establishes the room; shot-02 holds on the hero.", "shot outline",
            new List<SemanticCheckInput>
            {
                new("arc", "noul", "The outline shows a change in the hero's state.", null),
                new("pace", "score", "How is the pacing?", new List<string> { "dragging", "steady", "tight" }),
            });
        var record = await rig.Decisions.SemanticReview("proj", request, CancellationToken.None);
        Assert.Equal(DecisionOutcomes.Completed, record.Outcome);
        Assert.Null(record.SelectedCandidateId);
        Assert.Equal(2, record.Answers.Length);
        Assert.Equal(0.91, record.Answers[0].Noul);
        Assert.Equal(1.8, record.Answers[1].Score);
        Assert.Contains("Text-only semantic review", record.Reason);
        Assert.Contains("never replaces", record.Reason);
        Assert.Equal(1, provider.Calls);
        // The wire questions are honest noul/score shapes.
        var noul = provider.LastRequest!.Questions.Single(q => q.Id == "arc");
        Assert.Equal(DecisionQuestionKind.Noul, noul.Kind);
        Assert.Equal(new[] { "false", "true" }, noul.Candidates.Select(c => c.Id).OrderBy(x => x).ToArray());
        var score = provider.LastRequest.Questions.Single(q => q.Id == "pace");
        Assert.Equal(3, score.Candidates.Count); // the caller's legend
        // Identical repeat reuses the record without re-asking.
        var replay = await rig.Decisions.SemanticReview("proj", request, CancellationToken.None);
        Assert.Equal(record.InputHash, replay.InputHash);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task SemanticReviewDisabledOrUnconfiguredNeverCalls()
    {
        var provider = new TestRig.FakeProvider(Array.Empty<DecisionAnswer>());
        await using var rig = await ServiceRig.Start(provider);
        var request = new SemanticReviewRequest("req-18", "some text", null,
            new List<SemanticCheckInput> { new("ok", "noul", "The text is coherent.", null) });
        var unconfigured = await rig.Decisions.SemanticReview("proj", request, CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, unconfigured.Outcome);
        Assert.Contains("not configured", unconfigured.Reason);

        rig.Decisions.ConfigureProvider(ServiceRig.Settings);
        rig.Calibrate(p => p.SemanticReview.Enabled = false);
        var disabled = await rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("req-18b", request.Subject, null, request.Checks), CancellationToken.None);
        Assert.Equal(DecisionOutcomes.NeedsReview, disabled.Outcome);
        Assert.Contains("disabled", disabled.Reason);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task SemanticReviewValidatesChecks()
    {
        await using var rig = await ServiceRig.Start(null);
        var subject = "text";
        var noul = new SemanticCheckInput("ok", "noul", "Holds?", null);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-1", "", null, new List<SemanticCheckInput> { noul }), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-2", subject, null, new List<SemanticCheckInput>()), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-3", subject, null,
            new List<SemanticCheckInput> { new("x", "ranking", "Holds?", null) }), CancellationToken.None)); // unknown kind
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-4", subject, null,
            new List<SemanticCheckInput> { new("x", "noul", "Holds?", new List<string> { "a", "b" }) }), CancellationToken.None)); // noul takes no legend
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-5", subject, null,
            new List<SemanticCheckInput> { new("x", "score", "Rate?", new List<string> { "only" }) }), CancellationToken.None)); // legend too short
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-6", subject, null,
            new List<SemanticCheckInput> { noul, noul }), CancellationToken.None)); // duplicate ids
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Decisions.SemanticReview("proj", new SemanticReviewRequest("r-bad-7", subject, null,
            Enumerable.Range(0, SemanticReviewTask.MaxChecks + 1).Select(i => new SemanticCheckInput("c" + i, "noul", "Holds?", null)).ToList()), CancellationToken.None));
        Assert.False(rig.RecordExists(DecisionTasks.SemanticReview, "r-bad-1")); // caller errors are not audited as decisions
    }
}
