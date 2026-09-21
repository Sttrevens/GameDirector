using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Decisions;

/// <summary>One option offered to the decision model. The id is a stable
/// caller-owned key echoed back by the model; the description is the
/// natural-language criterion text for that option.</summary>
public sealed record DecisionCandidate(string Id, string Description);

/// <summary>Question kinds of the decisions contract. Choice and noul carry a
/// criteria map (candidateId → description); score carries a criteria array
/// whose index is the legend.</summary>
public enum DecisionQuestionKind { Choice, Score, Noul }

public sealed record DecisionQuestion(string Id, DecisionQuestionKind Kind, string Instructions, IReadOnlyList<DecisionCandidate> Candidates);

/// <summary>One validated answer. Choice answers carry Choice/Confidence and
/// per-candidate Probabilities. Noul answers carry Noul only (the contract has
/// no confidence on noul). Score answers carry Score/Confidence and per-legend
/// Probabilities.</summary>
public sealed record DecisionAnswer(
    string QuestionId,
    DecisionQuestionKind Kind,
    string? Choice,
    double? Confidence,
    double? Noul,
    double? Score,
    IReadOnlyDictionary<string, double> Probabilities)
{
    public string? ResolvedModel { get; init; }
    public string? ProviderRequestId { get; init; }
    public string? ResolvedProvider { get; init; }
    public decimal? Cost { get; init; }
}

/// <summary>A bounded, typed decision batch. State is small caller context
/// (intent, film summary) and must already be scrubbed of local paths.</summary>
public sealed record DecisionRequest(string Model, IReadOnlyDictionary<string, string> State, IReadOnlyList<DecisionQuestion> Questions)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > DecisionPolicyBounds.MaxModelChars)
            throw new ArgumentException("A model id (up to " + DecisionPolicyBounds.MaxModelChars + " characters) is required.");
        if (Questions == null || Questions.Count is < 1 or > DecisionPolicyBounds.MaxQuestions)
            throw new ArgumentException("A decision batch holds 1–" + DecisionPolicyBounds.MaxQuestions + " questions.");
        if (State != null && State.Count > DecisionPolicyBounds.MaxStateEntries)
            throw new ArgumentException("Decision state is limited to " + DecisionPolicyBounds.MaxStateEntries + " entries.");
        var ids = new HashSet<string>();
        foreach (var q in Questions)
        {
            if (q == null || string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 120 || !ids.Add(q.Id))
                throw new ArgumentException("Questions need unique non-empty ids.");
            if (string.IsNullOrWhiteSpace(q.Instructions) || q.Instructions.Length > DecisionPolicyBounds.MaxInstructionsChars)
                throw new ArgumentException("Question " + q.Id + " needs instructions (up to " + DecisionPolicyBounds.MaxInstructionsChars + " characters).");
            if (q.Candidates == null || q.Candidates.Count is < 1 or > DecisionPolicyBounds.MaxCandidatesPerQuestion)
                throw new ArgumentException("Question " + q.Id + " offers 1–" + DecisionPolicyBounds.MaxCandidatesPerQuestion + " candidates.");
            var candidateIds = new HashSet<string>();
            foreach (var c in q.Candidates)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 160 || !candidateIds.Add(c.Id))
                    throw new ArgumentException("Question " + q.Id + " candidates need unique non-empty ids.");
                if (string.IsNullOrWhiteSpace(c.Description) || c.Description.Length > DecisionPolicyBounds.MaxCandidateDescriptionChars)
                    throw new ArgumentException("Candidate " + c.Id + " needs a description (up to " + DecisionPolicyBounds.MaxCandidateDescriptionChars + " characters).");
            }
            if (q.Kind != DecisionQuestionKind.Score && q.Candidates.Count < 2)
                throw new ArgumentException("Question " + q.Id + " needs at least two candidates; a decision with one option is not a decision.");
            if (q.Kind == DecisionQuestionKind.Noul && !candidateIds.SetEquals(new[] { "false", "true" }))
                throw new ArgumentException("Noul criteria must use exactly the false and true keys.");
            if (!Enum.IsDefined(q.Kind)) throw new ArgumentException("Unknown decision question kind.");
        }
        if (State != null)
            foreach (var pair in State)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 80 || pair.Value == null || pair.Value.Length > DecisionPolicyBounds.MaxStateChars)
                    throw new ArgumentException("Decision state entries need short keys and values up to " + DecisionPolicyBounds.MaxStateChars + " characters.");
            }
    }
}

/// <summary>Submit one bounded batch of typed questions; return validated
/// answers. Implementations make a single bounded attempt — callers decide
/// whether anything is retried, and never retry with changed inputs.</summary>
public interface IDecisionProvider
{
    string ProviderId { get; }
    Task<IReadOnlyList<DecisionAnswer>> Decide(DecisionRequest request, CancellationToken ct);
}

/// <summary>Transport, protocol or contract failure while asking the provider.
/// Distinct from validation errors so callers can record provider failure as a
/// review outcome instead of mistaking it for a decision.</summary>
public sealed class DecisionProviderException : InvalidOperationException
{
    public DecisionProviderException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Bounds of one decision round-trip, named once so the settings UI,
/// the request validator and the HTTP client enforce the same conversation.</summary>
public static class DecisionPolicyBounds
{
    public const int MaxModelChars = 200;
    public const int MaxApiKeyChars = 4000;
    public const int MaxQuestions = 16;
    public const int MaxCandidatesPerQuestion = 32;
    public const int MaxInstructionsChars = 4000;
    public const int MaxCandidateDescriptionChars = 2000;
    public const int MaxStateEntries = 16;
    public const int MaxStateChars = 4000;
    public const int MaxIntentChars = 4000;
    public const int TimeoutSeconds = 60;
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Reserved candidate offered on choice questions so the model can
    /// honestly say nothing fits. Selecting it yields a no-match outcome.</summary>
    public const string NoneCandidateId = "none";
    public const string NoneCandidateDescription = "None of the offered candidates satisfy the intent.";
}
