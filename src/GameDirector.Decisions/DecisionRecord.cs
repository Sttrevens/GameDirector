using GameDirector.Client;

namespace GameDirector.Decisions;

/// <summary>One persisted decision receipt. Every ask — selected, needs-review
/// or no-match — commits one atomic record so results are auditable, dedupeable
/// and never silently re-asked with changed inputs.</summary>
public sealed class DecisionRecord
{
    public int Version { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public string Task { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string QuestionVersion { get; set; } = "";
    /// <summary>openrouter-jev, or "none" when no provider was actually called.</summary>
    public string ProviderId { get; set; } = "none";
    public string Model { get; set; } = "";
    /// <summary>Hash of every decision input: task, question version, intent,
    /// film/document identity, candidate ids+hashes, manifest/evidence hashes,
    /// model, per-task enabled flag and threshold, and the provider endpoint.
    /// The API key is a memory-only secret and is never hashed into identity:
    /// rotating a key must not orphan audit records. Reusing a requestId with
    /// a different hash conflicts.</summary>
    public string InputHash { get; set; } = "";
    public string? DocumentId { get; set; }
    public long? DocumentRevision { get; set; }
    public string FilmHash { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    /// <summary>Hash of the evidence base (performance catalog or media set).</summary>
    public string EvidenceHash { get; set; } = "";
    public string[] CandidateIds { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> CandidateHashes { get; set; } = new();
    /// <summary>The calibrated acceptance bar in effect, or null when the task
    /// was uncalibrated (every outcome is needs-review, nothing auto-selects).
    /// Null stays null in the audit — blank is a real state, not zero.</summary>
    public double? MinConfidence { get; set; }
    /// <summary>The provider endpoint the request went to (identity of the
    /// deployment, part of idempotency). Empty when no provider was configured.
    /// API keys are never recorded anywhere.</summary>
    public string ProviderEndpoint { get; set; } = "";
    public string Outcome { get; set; } = DecisionOutcomes.NeedsReview;
    public string Reason { get; set; } = "";
    public DecisionAnswerRecord[] Answers { get; set; } = Array.Empty<DecisionAnswerRecord>();
    public string? SelectedCandidateId { get; set; }
    /// <summary>Camera-choice only: the revised film with exactly one shot's
    /// camera replaced. A proposal, never auto-saved; document acceptance stays
    /// revision-aware through the normal save path.</summary>
    public FilmPlan? Proposal { get; set; }
    /// <summary>Film-ranking only: candidate scores in rank order.</summary>
    public DecisionRankingEntry[] Ranking { get; set; } = Array.Empty<DecisionRankingEntry>();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DecisionAnswerRecord
{
    public string QuestionId { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Choice { get; set; }
    public double? Confidence { get; set; }
    public double? Noul { get; set; }
    public double? Score { get; set; }
    public Dictionary<string, double> Probabilities { get; set; } = new();
    public string? ResolvedModel { get; set; }
    public string? ProviderRequestId { get; set; }
    public string? ResolvedProvider { get; set; }
    public decimal? Cost { get; set; }

    public static DecisionAnswerRecord From(DecisionAnswer a) => new()
    {
        QuestionId = a.QuestionId,
        Type = a.Kind.ToString().ToLowerInvariant(),
        Choice = a.Choice,
        Confidence = a.Confidence,
        Noul = a.Noul,
        Score = a.Score,
        Probabilities = new Dictionary<string, double>(a.Probabilities),
        ResolvedModel = a.ResolvedModel,
        ProviderRequestId = a.ProviderRequestId,
        ResolvedProvider = a.ResolvedProvider,
        Cost = a.Cost,
    };
}

public sealed class DecisionRankingEntry
{
    public string CandidateId { get; set; } = "";
    public double? Score { get; set; }
    public double? Confidence { get; set; }
    /// <summary>Compiler verdict offered to the model as evidence.</summary>
    public string Evidence { get; set; } = "";
}
