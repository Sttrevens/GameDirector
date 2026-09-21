using System.Text.Json.Nodes;
using GameDirector.Decisions;

namespace GameDirector.Decisions.Tests;

/// <summary>Wire contract tests against the verified official shape:
/// POST {model,state,questions:{id:{type,instructions,criteria}}} with a
/// criteria map for choice/noul and a criteria array for score; answers carry
/// choice+confidence+probabilities, score+confidence+probabilities, or noul
/// (no confidence) respectively.</summary>
public class JevWireTests
{
    [Fact]
    public void ChoiceRequestMatchesOfficialShape()
    {
        var request = TestRig.SampleRequest();
        var body = JevWire.BuildRequest(request);
        Assert.Equal("typesafe/jev-1.13", body["model"]!.GetValue<string>());
        Assert.Equal("pick one", body["state"]!["intent"]!.GetValue<string>());
        var question = body["questions"]!["cam"]!;
        Assert.Equal("choice", question["type"]!.GetValue<string>());
        Assert.Equal("Which camera?", question["instructions"]!.GetValue<string>());
        var criteria = question["criteria"] as JsonObject ?? throw new Xunit.Sdk.XunitException("choice criteria must be a map");
        Assert.Equal("Static wide from the north", criteria["cand-a"]!.GetValue<string>());
        Assert.Equal(DecisionPolicyBounds.NoneCandidateDescription, criteria[DecisionPolicyBounds.NoneCandidateId]!.GetValue<string>());
        // Exactly the official fields, nothing else.
        Assert.Equal(3, ((JsonObject)body).Count);
        Assert.Equal(3, ((JsonObject)question).Count);
    }

    [Fact]
    public void ScoreCriteriaTravelAsArray()
    {
        var request = new DecisionRequest("typesafe/jev-1.13", new Dictionary<string, string>(), new[]
        {
            new DecisionQuestion("rank-c1", DecisionQuestionKind.Score, "Rate this film.",
                FilmRankingTask.ScoreLegend.Select((legend, i) => new DecisionCandidate(i.ToString(), legend)).ToArray()),
        });
        var question = JevWire.BuildRequest(request)["questions"]!["rank-c1"]!;
        Assert.Equal("score", question["type"]!.GetValue<string>());
        var criteria = question["criteria"] as JsonArray ?? throw new Xunit.Sdk.XunitException("score criteria must be an array");
        Assert.Equal(FilmRankingTask.ScoreLegend.Length, criteria.Count);
        Assert.Equal(FilmRankingTask.ScoreLegend[3], criteria[3]!.GetValue<string>());
    }

    [Fact]
    public void NoulQuestionUsesCriteriaMap()
    {
        var request = new DecisionRequest("typesafe/jev-1.13", new Dictionary<string, string>(), new[]
        {
            new DecisionQuestion("is_bug", DecisionQuestionKind.Noul, "Is this a defect?", new[]
            {
                new DecisionCandidate("true", "Broken behavior reported"),
                new DecisionCandidate("false", "Question or feature request"),
            }),
        });
        var question = JevWire.BuildRequest(request)["questions"]!["is_bug"]!;
        Assert.Equal("noul", question["type"]!.GetValue<string>());
        Assert.IsType<JsonObject>(question["criteria"]);
    }

    [Fact]
    public void ParsesOfficialExampleResponse()
    {
        // The exact example from the official decisions documentation.
        const string official = """
        { "answers" : {
            "is_bug" : { "noul" : 0.96 , "type" : "noul" },
            "team" : { "choice" : "payments" , "confidence" : 0.75 , "probabilities" : { "account" : 0 , "frontend" : 0.16 , "payments" : 0.84 }, "type" : "choice" },
            "urgency" : { "confidence" : 0.99 , "legend" : { "0" : "Can wait" , "1" : "This week" , "2" : "Blocking" }, "probabilities" : { "0" : 0 , "1" : 0.01 , "2" : 0.99 }, "score" : 1.99 , "type" : "score" } } }
        """;
        var request = new DecisionRequest("typesafe/jev-1.13", new Dictionary<string, string>(), new[]
        {
            new DecisionQuestion("is_bug", DecisionQuestionKind.Noul, "defect?", new[]
            { new DecisionCandidate("true", "broken"), new DecisionCandidate("false", "question") }),
            new DecisionQuestion("team", DecisionQuestionKind.Choice, "owner?", new[]
            { new DecisionCandidate("account", "a"), new DecisionCandidate("frontend", "f"), new DecisionCandidate("payments", "p") }),
            new DecisionQuestion("urgency", DecisionQuestionKind.Score, "urgent?", new[]
            { new DecisionCandidate("0", "Can wait"), new DecisionCandidate("1", "This week"), new DecisionCandidate("2", "Blocking") }),
        });
        var answers = JevWire.ParseAnswers(JsonNode.Parse(official), request);
        var noul = answers.Single(a => a.QuestionId == "is_bug");
        Assert.Equal(0.96, noul.Noul);
        Assert.Null(noul.Confidence); // noul answers carry noul, NOT confidence
        Assert.Null(noul.Choice);
        var team = answers.Single(a => a.QuestionId == "team");
        Assert.Equal("payments", team.Choice);
        Assert.Equal(0.75, team.Confidence);
        Assert.Equal(0.84, team.Probabilities["payments"]);
        var urgency = answers.Single(a => a.QuestionId == "urgency");
        Assert.Equal(1.99, urgency.Score);
        Assert.Equal(0.99, urgency.Confidence);
        Assert.Equal(0.99, urgency.Probabilities["2"]);
    }

    [Fact]
    public void RejectsUnofferedChoice()
    {
        var request = TestRig.SampleRequest();
        var body = JsonNode.Parse("""{ "answers": { "cam": { "type": "choice", "choice": "never-offered", "confidence": 0.9 } } }""");
        var ex = Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(body, request));
        Assert.Contains("unoffered", ex.Message);
    }

    [Fact]
    public void RejectsMalformedResponses()
    {
        var request = TestRig.SampleRequest();
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse("""{ "answers": {} }"""), request)); // missing question
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse("""{}"""), request)); // no answers object
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse(
            """{ "answers": { "cam": { "type": "noul", "noul": 0.5 }, "ghost": { "type": "noul", "noul": 0.5 } } }"""), request)); // unasked question
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse(
            """{ "answers": { "cam": { "type": "noul", "noul": 0.5 } } }"""), request)); // wrong answer type
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse(
            """{ "answers": { "cam": { "type": "choice", "choice": "cand-a", "confidence": 1.4 } } }"""), request)); // confidence outside [0,1]
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse(
            """{ "answers": { "cam": { "type": "choice", "choice": "cand-a", "confidence": 0.9, "probabilities": { "never-offered": 0.5 } } } }"""), request)); // probability for unoffered candidate
    }

    [Fact]
    public void ValidatesRequestBounds()
    {
        Assert.Throws<ArgumentException>(() => new DecisionRequest("", new Dictionary<string, string>(), TestRig.SampleRequest().Questions).Validate());
        Assert.Throws<ArgumentException>(() => new DecisionRequest("m", new Dictionary<string, string>(), Array.Empty<DecisionQuestion>()).Validate());
        // A choice question with a single candidate is not a decision.
        Assert.Throws<ArgumentException>(() => new DecisionRequest("m", new Dictionary<string, string>(), new[]
        { new DecisionQuestion("q", DecisionQuestionKind.Choice, "i", new[] { new DecisionCandidate("a", "only one") }) }).Validate());
        // Duplicate question ids are rejected.
        Assert.Throws<ArgumentException>(() => new DecisionRequest("m", new Dictionary<string, string>(), new[]
        {
            new DecisionQuestion("q", DecisionQuestionKind.Choice, "i", new[] { new DecisionCandidate("a", "1"), new DecisionCandidate("b", "2") }),
            new DecisionQuestion("q", DecisionQuestionKind.Choice, "i", new[] { new DecisionCandidate("a", "1"), new DecisionCandidate("b", "2") }),
        }).Validate());
    }

    [Fact]
    public void ScoreAnswerBeyondLegendIsRejected()
    {
        var request = new DecisionRequest("m", new Dictionary<string, string>(), new[]
        { new DecisionQuestion("s", DecisionQuestionKind.Score, "i", new[] { new DecisionCandidate("0", "low"), new DecisionCandidate("1", "high") }) });
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse(
            """{ "answers": { "s": { "type": "score", "score": 9, "confidence": 0.9 } } }"""), request));
    }
}
