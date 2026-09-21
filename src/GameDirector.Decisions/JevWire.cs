using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameDirector.Decisions;

/// <summary>Wire format of the OpenRouter alpha decisions endpoint (Jev).
/// Request:  POST {model, state, questions:{id:{type, instructions, criteria}}}
///           where criteria is a candidateId→description map for choice/noul
///           and a legend description array for score.
/// Response: {answers:{id:{type:'choice',choice,confidence,probabilities}
///                      | {type:'score',score,confidence,legend,probabilities}
///                      | {type:'noul',noul}}}  — noul answers carry no confidence.
/// Only this file knows the JSON shape; provider and tasks stay typed.</summary>
public static class JevWire
{
    public static JsonObject BuildRequest(DecisionRequest request)
    {
        request.Validate();
        var questions = new JsonObject();
        foreach (var q in request.Questions)
        {
            JsonNode criteria = q.Kind == DecisionQuestionKind.Score
                ? new JsonArray(q.Candidates.Select(c => (JsonNode)JsonValue.Create(c.Description)).ToArray())
                : BuildCriteriaMap(q.Candidates);
            questions[q.Id] = new JsonObject
            {
                ["type"] = q.Kind.ToString().ToLowerInvariant(),
                ["instructions"] = q.Instructions,
                ["criteria"] = criteria,
            };
        }
        var body = new JsonObject { ["model"] = request.Model, ["questions"] = questions, ["state"] = new JsonObject() };
        if (request.State != null && request.State.Count > 0)
        {
            var state = new JsonObject();
            foreach (var pair in request.State) state[pair.Key] = pair.Value;
            body["state"] = state;
        }
        return body;
    }

    private static JsonObject BuildCriteriaMap(IReadOnlyList<DecisionCandidate> candidates)
    {
        var map = new JsonObject();
        foreach (var c in candidates) map[c.Id] = c.Description;
        return map;
    }

    public static IReadOnlyList<DecisionAnswer> ParseAnswers(JsonNode? body, DecisionRequest request)
    {
        if (body is not JsonObject envelope) throw new DecisionProviderException("Decision response must be an object.");
        var answers = envelope["answers"] as JsonObject
            ?? throw new DecisionProviderException("Decision response has no answers object.");
        var result = new List<DecisionAnswer>(request.Questions.Count);
        foreach (var question in request.Questions)
        {
            if (answers[question.Id] is not JsonObject answer)
                throw new DecisionProviderException("Decision response is missing an answer for question " + question.Id + ".");
            result.Add(ParseAnswer(question, answer) with
            {
                ResolvedModel = OptionalText(envelope, "model"),
                ProviderRequestId = OptionalText(envelope, "id"),
                ResolvedProvider = OptionalText(envelope, "provider"),
                Cost = envelope["usage"] is JsonObject usage && usage["cost"] is JsonValue cost && cost.TryGetValue<decimal>(out var amount) ? amount : null
            });
        }
        foreach (var key in answers.Select(p => p.Key))
            if (request.Questions.All(q => q.Id != key))
                throw new DecisionProviderException("Decision response answered an unasked question: " + key + ".");
        return result;
    }

    private static DecisionAnswer ParseAnswer(DecisionQuestion question, JsonObject answer)
    {
        var type = OptionalText(answer, "type");
        var expected = question.Kind.ToString().ToLowerInvariant();
        if (type != expected)
            throw new DecisionProviderException("Answer " + question.Id + " has type " + (type ?? "null") + "; expected " + expected + ".");
        var offered = question.Candidates.Select(c => c.Id).ToHashSet();
        switch (question.Kind)
        {
            case DecisionQuestionKind.Choice:
            {
                var choice = OptionalText(answer, "choice");
                if (choice == null || !offered.Contains(choice))
                    throw new DecisionProviderException("Answer " + question.Id + " selected an unoffered candidate: " + (choice ?? "null") + ".");
                var confidence = RequiredUnit(answer, "confidence", question.Id);
                var probabilities = Probabilities(answer, question.Id, offered);
                return new DecisionAnswer(question.Id, question.Kind, choice, confidence, null, null, probabilities);
            }
            case DecisionQuestionKind.Score:
            {
                var score = RequiredNumber(answer, "score", question.Id);
                if (!double.IsFinite(score) || score < 0 || score > question.Candidates.Count - 1)
                    throw new DecisionProviderException("Answer " + question.Id + " score is outside the offered legend.");
                var confidence = RequiredUnit(answer, "confidence", question.Id);
                var legendIndices = Enumerable.Range(0, question.Candidates.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
                var probabilities = Probabilities(answer, question.Id, legendIndices);
                return new DecisionAnswer(question.Id, question.Kind, null, confidence, null, score, probabilities);
            }
            case DecisionQuestionKind.Noul:
            {
                // The contract: noul answers are {type:'noul', noul: 0.96}. There is
                // no confidence field on noul; read noul and nothing else.
                var noul = RequiredUnit(answer, "noul", question.Id);
                return new DecisionAnswer(question.Id, question.Kind, null, null, noul, null, new Dictionary<string, double>());
            }
            default:
                throw new DecisionProviderException("Unknown question kind for " + question.Id + ".");
        }
    }

    private static string? OptionalText(JsonObject value, string field)
    {
        if (value[field] == null) return null;
        if (value[field] is JsonValue text && text.TryGetValue<string>(out var result)) return result;
        throw new DecisionProviderException("Decision response field " + field + " must be text.");
    }

    private static double RequiredNumber(JsonObject answer, string field, string questionId)
    {
        var node = answer[field];
        if (node == null || node is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number))
            throw new DecisionProviderException("Answer " + questionId + " is missing a finite " + field + ".");
        return number;
    }

    private static double RequiredUnit(JsonObject answer, string field, string questionId)
    {
        var number = RequiredNumber(answer, field, questionId);
        if (number < 0 || number > 1)
            throw new DecisionProviderException("Answer " + questionId + " " + field + " is outside [0,1].");
        return number;
    }

    private static IReadOnlyDictionary<string, double> Probabilities(JsonObject answer, string questionId, HashSet<string> offered)
    {
        var result = new Dictionary<string, double>();
        if (answer["probabilities"] is not JsonObject probabilities) return result;
        foreach (var pair in probabilities)
        {
            if (!offered.Contains(pair.Key))
                throw new DecisionProviderException("Answer " + questionId + " scored an unoffered candidate: " + pair.Key + ".");
            if (pair.Value is not JsonValue value || !value.TryGetValue<double>(out var p) || !double.IsFinite(p) || p < 0 || p > 1)
                throw new DecisionProviderException("Answer " + questionId + " has an invalid probability for " + pair.Key + ".");
            result[pair.Key] = p;
        }
        return result;
    }

    /// <summary>Serialize with the exact bytes we send, so size caps and request
    /// hashes cover the real payload.</summary>
    public static string SerializeRequest(JsonObject request) => request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
