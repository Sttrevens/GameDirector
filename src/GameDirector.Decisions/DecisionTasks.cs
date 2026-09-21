using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Decisions;

/// <summary>A camera configuration offered for one exact shot. Supplied by the
/// caller (UI or external agent); never invented by the decision layer.</summary>
public sealed class CameraCandidate
{
    public string Id { get; set; } = "";
    public ShotSpec Camera { get; set; } = new();
}

/// <summary>An imported sound offered for an audio match. A sound without an
/// authored description is never offered: a file name alone says nothing about
/// how a sound plays.</summary>
public sealed record AudioCandidate(string MediaId, string Name, string? Description, double DurationSeconds);

/// <summary>A film submitted for ranking, with the submitter's optional rationale.</summary>
public sealed class FilmRankingCandidate
{
    public string Id { get; set; } = "";
    public string? Rationale { get; set; }
    public FilmPlan Film { get; set; } = new();
}

/// <summary>Question builders for the decision tasks. Builders own prompt text,
/// candidate descriptions and eligibility rules; they never perform I/O and
/// never call a provider. All free text is scrubbed of local paths. A builder
/// throws when handed more candidates than the task's bound: the service must
/// resolve excess explicitly (needs-review) instead of silently truncating.</summary>
public static class PerformanceMatchTask
{
    public const string QuestionVersion = "performance-match/v1";
    public const int MaxCandidates = 24;

    /// <summary>Entries eligible as candidates: claims backed by evidence. The
    /// full eligible set is returned; bounding is the caller's explicit
    /// decision. Catalog freshness against the live manifest is the caller's
    /// gate (CatalogTools.Validate); here we only require describable meaning.</summary>
    public static IReadOnlyList<PerformanceEntry> Eligible(PerformanceCatalog catalog) =>
        catalog.Performances
            .Where(p => p != null && p.Evidence != null && p.Evidence.Count > 0 &&
                (!string.IsNullOrWhiteSpace(p.Meaning) || (p.UsefulFor != null && p.UsefulFor.Count > 0) || (p.Constraints != null && p.Constraints.Count > 0)))
            .ToArray();

    /// <summary>Unambiguous candidate id: actor and clip may themselves contain
    /// the separator, so the actor length prefixes the pair. "a/b"+"c" and
    /// "a"+"b/c" can never collide.</summary>
    public static string CandidateId(PerformanceEntry entry) =>
        entry.Actor.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + entry.Actor + "/" + entry.Clip;

    public static string Describe(PerformanceEntry entry, params string?[] roots)
    {
        var scrub = (string? text) => LocalPathScrubber.Scrub(text, roots);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Meaning)) parts.Add("meaning: " + scrub(entry.Meaning));
        if (entry.DurationSeconds is > 0) parts.Add("duration: " + entry.DurationSeconds.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s");
        if (entry.UsefulFor?.Count > 0) parts.Add("useful for: " + scrub(string.Join(", ", entry.UsefulFor)));
        if (entry.Constraints?.Count > 0) parts.Add("constraints: " + scrub(string.Join(", ", entry.Constraints)));
        var evidence = entry.Evidence.Select(e => e.Kind + " " + scrub(e.Source) + (e.Start.HasValue ? " @" + e.Start.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "–" + e.End!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" : ""));
        parts.Add("evidence: " + string.Join("; ", evidence));
        return string.Join(". ", parts);
    }

    public static DecisionQuestion BuildQuestion(string intent, IReadOnlyList<PerformanceEntry> eligible, params string?[] roots)
    {
        if (eligible.Count > MaxCandidates)
            throw new ArgumentException("Performance matching offers at most " + MaxCandidates + " candidates; resolve excess explicitly instead of truncating.");
        var candidates = eligible
            .Select(e => new DecisionCandidate(CandidateId(e), Describe(e, roots)))
            .Append(new DecisionCandidate(DecisionPolicyBounds.NoneCandidateId, DecisionPolicyBounds.NoneCandidateDescription))
            .ToArray();
        return new DecisionQuestion(
            "performance",
            DecisionQuestionKind.Choice,
            "Choose the catalog performance whose evidence-backed meaning best fits this intent: \"" + LocalPathScrubber.Scrub(intent, roots) +
                "\". Judge only from the stated meanings and evidence; a clip name alone is not observed fact. Choose none when no entry fits.",
            candidates);
    }
}

public static class CameraChoiceTask
{
    public const string QuestionVersion = "camera-choice/v1";
    public const int MaxCandidates = 16;

    public static FilmShot FindShot(FilmPlan film, string shotId) =>
        film.Scenes.SelectMany(s => s.Shots).FirstOrDefault(x => x.Id == shotId)
        ?? throw new KeyNotFoundException("Shot not found: " + shotId);

    /// <summary>Compiler validation of one candidate in the real shot context:
    /// the full film with only that shot's camera replaced must compile against
    /// the live manifest. Returns null when compilable, else the reason.</summary>
    public static string? ValidateCandidate(FilmPlan film, string shotId, ShotSpec camera, CapabilityManifest manifest)
    {
        try { FilmCompiler.Prepare(Apply(film, shotId, camera), manifest); return null; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
    }

    /// <summary>Exact shot-local revision: deep-copies the film and replaces one
    /// shot's camera. Every other shot, performance, audio cue and subtitle is
    /// byte-identical. Shot ids, timing and purposes are never touched.</summary>
    public static FilmPlan Apply(FilmPlan film, string shotId, ShotSpec camera)
    {
        var revised = DslJson.Deserialize<FilmPlan>(DslJson.Serialize(film));
        var shot = FindShot(revised, shotId);
        var replacement = DslJson.Deserialize<ShotSpec>(DslJson.Serialize(camera));
        // Editorial duration derives from the shot's start/end at compile time;
        // candidates must not smuggle timing changes into a camera decision.
        replacement.DurationSeconds = 0;
        shot.Camera = replacement;
        return revised;
    }

    /// <summary>Manifest-vocabulary candidates for a shot: the declared shot
    /// types × frame sizes, in manifest declaration order with no frame
    /// preference, anchored at the shot's own subject and anchors. This is a
    /// bounded deterministic subset of the vocabulary (at most MaxCandidates);
    /// the question text discloses that. All entries are compiler-checked by
    /// the caller before being offered.</summary>
    public static IReadOnlyList<CameraCandidate> Suggest(CapabilityManifest manifest, FilmShot shot)
    {
        var subject = shot.Camera?.Subject ?? manifest.Roles.FirstOrDefault(r => r.PresentAtStart)?.Id ?? manifest.Roles.FirstOrDefault()?.Id;
        var from = string.IsNullOrWhiteSpace(shot.Camera?.From) ? "current" : shot.Camera.From;
        var results = new List<CameraCandidate>();
        var seen = new HashSet<string>();
        void Add(ShotSpec spec)
        {
            if (results.Count >= MaxCandidates) return;
            var id = spec.Type + "-" + spec.Frame + "-" + spec.From + (spec.To != null && spec.To != "current" ? "-" + spec.To : "");
            if (!seen.Add(id)) return;
            results.Add(new CameraCandidate { Id = id, Camera = spec });
        }
        var target = manifest.Locations.FirstOrDefault(l => l.Id != from)?.Id;
        foreach (var frame in manifest.FrameTypes)
            foreach (var type in manifest.ShotTypes)
            {
                string? to = null;
                if (ShotVocabulary.RequiresTarget(type) == true)
                {
                    if (target == null) continue; // moving shot needs a destination anchor
                    to = target;
                }
                Add(new ShotSpec { Type = type, Subject = subject, Frame = frame, From = from, To = to });
            }
        return results;
    }

    public static string Describe(CameraCandidate candidate, CapabilityManifest manifest)
    {
        var c = candidate.Camera;
        var parts = new List<string> { "shot type " + c.Type, "framing " + c.Frame, "subject " + c.Subject };
        parts.Add("starts from " + (c.From == "current" ? "the current framing anchor" : "anchor " + c.From));
        if (!string.IsNullOrWhiteSpace(c.To) && c.To != "current") parts.Add("moves to anchor " + c.To);
        if (c.LookAt != null) parts.Add("looks at " + c.LookAt);
        if (c.Ease != null && c.Ease != "inOut") parts.Add("easing " + c.Ease);
        if (c.Fov is float fov) parts.Add("fov " + fov.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(", ", parts) + ".";
    }

    public static DecisionQuestion BuildQuestion(FilmShot shot, string intent, IReadOnlyList<CameraCandidate> eligible, CapabilityManifest manifest, params string?[] roots)
    {
        if (eligible.Count > MaxCandidates)
            throw new ArgumentException("Camera choice offers at most " + MaxCandidates + " candidates; resolve excess explicitly instead of truncating.");
        var candidates = eligible
            .Select(c => new DecisionCandidate(c.Id, Describe(c, manifest)))
            .Append(new DecisionCandidate(DecisionPolicyBounds.NoneCandidateId, DecisionPolicyBounds.NoneCandidateDescription))
            .ToArray();
        return new DecisionQuestion(
            "camera",
            DecisionQuestionKind.Choice,
            "Choose the camera configuration that best serves this shot's purpose: \"" + LocalPathScrubber.Scrub(shot.Purpose, roots) +
                "\". Director's intent: \"" + LocalPathScrubber.Scrub(intent, roots) +
                "\". Offered cameras are a bounded deterministic subset of the live manifest vocabulary (at most " + MaxCandidates +
                "), each verified to compile in this shot's context; judge cinematically, not by coordinates. Choose none when no offered camera serves the intent.",
            candidates);
    }
}

public static class AudioMatchTask
{
    public const string QuestionVersion = "audio-match/v1";
    public const int MaxCandidates = 24;

    /// <summary>Sounds eligible to offer: imported media with an authored
    /// description. A sound is never offered on its file name alone — a name
    /// is not evidence of how the sound plays.</summary>
    public static IReadOnlyList<AudioCandidate> Eligible(IEnumerable<AudioCandidate> library) =>
        library.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Description)).ToArray();

    public static DecisionQuestion BuildQuestion(string intent, string? bus, IReadOnlyList<AudioCandidate> offered, params string?[] roots)
    {
        if (offered.Count > MaxCandidates)
            throw new ArgumentException("Audio matching offers at most " + MaxCandidates + " candidates; resolve excess explicitly instead of truncating.");
        if (offered.Any(a => string.IsNullOrWhiteSpace(a.Description)))
            throw new ArgumentException("Audio candidates need an authored description; names alone are not evidence.");
        var scrub = (string? text) => LocalPathScrubber.Scrub(text, roots);
        var candidates = offered
            .Select(a => new DecisionCandidate(a.MediaId,
                "\"" + scrub(a.Name) + "\" — " + a.DurationSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s imported sound. Description: " + scrub(a.Description)))
            .Append(new DecisionCandidate(DecisionPolicyBounds.NoneCandidateId, DecisionPolicyBounds.NoneCandidateDescription))
            .ToArray();
        return new DecisionQuestion(
            "audio",
            DecisionQuestionKind.Choice,
            "Choose the imported sound that best fits this intent: \"" + scrub(intent) + "\"" +
                (string.IsNullOrWhiteSpace(bus) ? "" : " for the " + scrub(bus) + " bus") +
                ". Judge only from the supplied descriptions; a file name alone is not evidence of how a sound plays. The film mixes these exact files. Choose none when nothing fits.",
            candidates);
    }
}

public static class FilmRankingTask
{
    public const string QuestionVersion = "film-ranking/v1";
    public const int MaxCandidates = 8;

    /// <summary>Score legend; criteria travel as this array, index is the score.</summary>
    public static readonly string[] ScoreLegend =
    {
        "Does not satisfy the brief",
        "Partially satisfies the brief",
        "Mostly satisfies the brief",
        "Fully satisfies the brief",
    };

    public const string ReviewNote =
        "Text and evidence review only: titles, shot purposes, structure and compiler verdicts were evaluated. " +
        "No rendered footage was reviewed; this is not a visual judgment.";

    /// <summary>Compiler verdict as evidence text, or null when the candidate
    /// cannot compile against the live manifest (ineligible).</summary>
    public static string? CompileEvidence(FilmRankingCandidate candidate, CapabilityManifest manifest, out string? ineligibilityReason)
    {
        try
        {
            var prepared = FilmCompiler.Prepare(candidate.Film, manifest);
            var shots = prepared.Shots.Count;
            var seconds = prepared.Shots.Sum(s => s.End - s.Start);
            ineligibilityReason = null;
            return "Compiles against the live manifest: " + candidate.Film.Scenes.Count + " scenes, " + shots + " shots, " +
                seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s final film.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ineligibilityReason = "does not compile: " + ex.Message;
            return null;
        }
    }

    public static DecisionQuestion BuildQuestion(string brief, FilmRankingCandidate candidate, string compileEvidence, params string?[] roots)
    {
        var scrub = (string? text) => LocalPathScrubber.Scrub(text, roots);
        var film = candidate.Film;
        var outline = string.Join("; ", film.Scenes.SelectMany(s => s.Shots).Take(40).Select(x => x.Id + " (" + x.Start.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "–" + x.End.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s): " + scrub(x.Purpose)));
        var description = "Title: \"" + scrub(film.Title) + "\". " + compileEvidence +
            (string.IsNullOrWhiteSpace(candidate.Rationale) ? "" : " Submitter's rationale: " + scrub(candidate.Rationale) + ".") +
            " Shot outline: " + outline;
        return new DecisionQuestion(
            "rank-" + candidate.Id,
            DecisionQuestionKind.Score,
            "Rate how well this candidate film satisfies the brief: \"" + scrub(brief) +
                "\". Base the rating only on authored text and compiler evidence; no rendered footage exists.",
            ScoreLegend.Select((legend, i) => new DecisionCandidate(i.ToString(System.Globalization.CultureInfo.InvariantCulture), legend)).ToArray());
    }

    /// <summary>Rank candidates by score, ties broken by confidence. Deterministic
    /// for equal answers: candidate id ascending.</summary>
    public static DecisionRankingEntry[] Rank(IReadOnlyList<DecisionAnswer> answers, IReadOnlyList<FilmRankingCandidate> candidates, IReadOnlyDictionary<string, string> evidence)
    {
        return candidates
            .Select(c =>
            {
                var answer = answers.FirstOrDefault(a => a.QuestionId == "rank-" + c.Id);
                return new DecisionRankingEntry { CandidateId = c.Id, Score = answer?.Score, Confidence = answer?.Confidence, Evidence = evidence.TryGetValue(c.Id, out var e) ? e : "" };
            })
            .OrderByDescending(e => e.Score ?? -1)
            .ThenByDescending(e => e.Confidence ?? -1)
            .ThenBy(e => e.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>Builder for the optional semantic-review task: caller-requested
/// text-only checks (noul true/false judgments or ordinal scale ratings) over
/// a supplied text. The task never selects or applies anything, never looks at
/// rendered footage, and never replaces compilation or rendering verification.</summary>
public static class SemanticReviewTask
{
    public const string QuestionVersion = "semantic-review/v1";
    public const int MaxChecks = 8;
    public const int MaxSubjectChars = 2500;
    public const int MaxCheckTextChars = 800;
    public const int MaxLabelChars = 200;
    public const int MinLegend = 2;
    public const int MaxLegend = 8;
    public const int MaxLegendEntryChars = 200;

    public const string NoulTrueDescription = "The statement holds for the reviewed text.";
    public const string NoulFalseDescription = "The statement does not hold, or cannot be determined from the text.";

    public const string ReviewNote =
        "Text-only semantic review: the supplied text was checked against the requested criteria. " +
        "No rendered video, audio or gameplay was judged, and this never replaces compiling, rendering or playthrough verification.";

    /// <summary>One requested check expressed as a decision question. Noul
    /// checks carry the fixed false/true criteria; score checks carry the
    /// caller's legend as the ordinal criteria array (index is the score).</summary>
    public static DecisionQuestion BuildCheck(string checkId, string kind, string question, IReadOnlyList<string>? legend, string subject, string? label, params string?[] roots)
    {
        var scrub = (string? text) => LocalPathScrubber.Scrub(text, roots);
        var subjectText = scrub(subject);
        var prompt = "Reviewed text" + (string.IsNullOrWhiteSpace(label) ? "" : " (" + scrub(label) + ")") + ": \"" + subjectText + "\"\n\nCheck: " + scrub(question);
        if (kind == "noul")
            return new DecisionQuestion(checkId, DecisionQuestionKind.Noul,
                prompt + "\nAnswer only whether the statement holds for the text; do not assume facts outside it.",
                new[] { new DecisionCandidate("false", NoulFalseDescription), new DecisionCandidate("true", NoulTrueDescription) });
        if (kind == "score")
        {
            if (legend == null || legend.Count is < MinLegend or > MaxLegend)
                throw new ArgumentException("Score check " + checkId + " needs a legend of " + MinLegend + "–" + MaxLegend + " ordered labels.");
            if (legend.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > MaxLegendEntryChars))
                throw new ArgumentException("Score check " + checkId + " legend labels need 1–" + MaxLegendEntryChars + " characters.");
            return new DecisionQuestion(checkId, DecisionQuestionKind.Score,
                prompt + "\nRate the text on the ordinal scale whose labels are the criteria array; index 0 is the first label.",
                legend.Select((l, i) => new DecisionCandidate(i.ToString(System.Globalization.CultureInfo.InvariantCulture), scrub(l))).ToArray());
        }
        throw new ArgumentException("Check " + checkId + " kind must be noul or score.");
    }
}
