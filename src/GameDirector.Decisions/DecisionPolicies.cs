namespace GameDirector.Decisions;

/// <summary>Stable task identifiers and question-schema versions. A question
/// version bumps whenever the prompt/criteria shape changes, so audit records
/// stay interpretable and dedupe never mixes question generations.</summary>
public static class DecisionTasks
{
    public const string PerformanceMatch = "performance-match";
    public const string CameraChoice = "camera-choice";
    public const string AudioMatch = "audio-match";
    public const string FilmRanking = "film-ranking";
    public const string SemanticReview = "semantic-review";
    public static readonly string[] All = { PerformanceMatch, CameraChoice, AudioMatch, FilmRanking, SemanticReview };
}

public static class DecisionOutcomes
{
    /// <summary>Provider answer passed policy; the proposal is attached.</summary>
    public const string Selected = "selected";
    /// <summary>No automatic pick: low confidence, uncalibrated policy, malformed
    /// or unoffered answer, provider failure, disabled task or missing
    /// configuration. The original film is always preserved.</summary>
    public const string NeedsReview = "needs-review";
    /// <summary>No eligible candidate exists (empty catalog, empty library,
    /// no compilable camera) or the model chose the offered none option.</summary>
    public const string NoMatch = "no-match";
    /// <summary>Semantic review only: the requested text checks were answered
    /// and are attached. Nothing is selected or applied; a human reads the
    /// answers. Review answers are text judgments, never rendered-footage
    /// verification.</summary>
    public const string Completed = "completed";
}

/// <summary>Per-task switch and acceptance bar.
///
/// MinConfidence is deliberately nullable. Null means UNCALIBRATED: the task
/// may still ask the provider when explicitly invoked, but every answer lands
/// in needs-review for a human and is never auto-selected. There is no
/// universal magic cutoff — a confidence number only means something relative
/// to one provider, one model and one task, measured on real outcomes. Set an
/// explicit per-task threshold only after evaluating that pairing; the
/// decision layer ships without invented defaults. Blank stays blank in the
/// UI, in the persisted policies and in audit records.</summary>
public sealed class DecisionTaskPolicy
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minimum answer confidence for an automatic selected outcome, or
    /// null when uncalibrated. Below the bar — always, when null — the record is
    /// needs-review with the answers attached.</summary>
    public double? MinConfidence { get; set; }

    public DecisionTaskPolicy Clone() => new() { Enabled = Enabled, MinConfidence = MinConfidence };
}

public sealed class DecisionPolicies
{
    public int Version { get; set; } = 1;

    // All tasks start enabled but uncalibrated (null threshold): nothing is
    // auto-selected until an explicit per-task bar is configured.
    public DecisionTaskPolicy PerformanceMatch { get; set; } = new();
    public DecisionTaskPolicy CameraChoice { get; set; } = new();
    public DecisionTaskPolicy AudioMatch { get; set; } = new();
    public DecisionTaskPolicy FilmRanking { get; set; } = new();
    /// <summary>Semantic review never auto-applies anything, so only the
    /// enabled flag is meaningful; a threshold may still be recorded for audit
    /// context but never gates an outcome.</summary>
    public DecisionTaskPolicy SemanticReview { get; set; } = new();

    public DecisionTaskPolicy For(string task) => task switch
    {
        DecisionTasks.PerformanceMatch => PerformanceMatch,
        DecisionTasks.CameraChoice => CameraChoice,
        DecisionTasks.AudioMatch => AudioMatch,
        DecisionTasks.FilmRanking => FilmRanking,
        DecisionTasks.SemanticReview => SemanticReview,
        _ => throw new ArgumentException("Unknown decision task: " + task),
    };

    public void Validate()
    {
        foreach (var task in DecisionTasks.All)
        {
            var policy = For(task);
            if (policy.MinConfidence is double bar && (!double.IsFinite(bar) || bar < 0 || bar > 1))
                throw new ArgumentException("Task " + task + " confidence threshold must be within [0,1], or empty (uncalibrated).");
        }
    }

    public DecisionPolicies Clone() => new()
    {
        Version = Version,
        PerformanceMatch = PerformanceMatch.Clone(),
        CameraChoice = CameraChoice.Clone(),
        AudioMatch = AudioMatch.Clone(),
        FilmRanking = FilmRanking.Clone(),
        SemanticReview = SemanticReview.Clone(),
    };
}
