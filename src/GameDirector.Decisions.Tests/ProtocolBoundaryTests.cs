using System.Text.Json.Nodes;
using GameDirector.Decisions;
using Xunit;

public sealed class ProtocolBoundaryTests
{
    private static DecisionRequest Request(DecisionQuestionKind kind, params string[] ids) => new(
        "test-model", new Dictionary<string, string>(),
        new[] { new DecisionQuestion("pick", kind, "Evaluate the supplied candidates.",
            ids.Select(id => new DecisionCandidate(id, "Meaning of " + id)).ToArray()) });

    [Fact]
    public void StateIsAlwaysPresentBecauseTheWireContractRequiresIt()
    {
        var wire = JevWire.BuildRequest(Request(DecisionQuestionKind.Choice, "a", "b"));
        Assert.IsType<JsonObject>(wire["state"]);
    }

    [Fact]
    public void ScoreUsesLegendIndicesRatherThanCallerCandidateIds()
    {
        var request = Request(DecisionQuestionKind.Score, "poor", "good");
        var response = JsonNode.Parse("""{"answers":{"pick":{"type":"score","score":0.8,"confidence":0.6,"probabilities":{"0":0.2,"1":0.8},"legend":{"0":"Meaning of poor","1":"Meaning of good"}}}}""");
        var answer = Assert.Single(JevWire.ParseAnswers(response, request));
        Assert.Equal(0.8, answer.Score);
        Assert.Equal(0.8, answer.Probabilities["1"]);
    }

    [Fact]
    public void ScoreAboveHighestLegendIndexIsRejected()
    {
        var request = Request(DecisionQuestionKind.Score, "0", "1");
        var response = JsonNode.Parse("""{"answers":{"pick":{"type":"score","score":1.4,"confidence":0.6,"probabilities":{"0":0.2,"1":0.8}}}}""");
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(response, request));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"answers\":{\"pick\":{\"type\":123}}}")]
    [InlineData("{\"answers\":{\"pick\":{\"type\":\"choice\",\"choice\":42}}}")]
    public void MalformedProviderTypesAreReportedAsProviderFailures(string json)
    {
        Assert.Throws<DecisionProviderException>(() => JevWire.ParseAnswers(JsonNode.Parse(json), Request(DecisionQuestionKind.Choice, "a", "b")));
    }

    [Fact]
    public void NoulCannotSendArbitraryCandidateKeys()
    {
        Assert.Throws<ArgumentException>(() => JevWire.BuildRequest(Request(DecisionQuestionKind.Noul, "a", "b")));
    }
}
