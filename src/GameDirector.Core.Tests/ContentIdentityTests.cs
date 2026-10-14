using System.Text.Json.Nodes;
using GameDirector.Client;
using Xunit;

namespace GameDirector.Core.Tests;

public class ContentIdentityTests
{
    [Fact] public void KeyOrderDoesNotChangeIdentity()
    {
        var a = JsonNode.Parse("{\"b\":1,\"a\":{\"y\":[2,3],\"x\":\"s\"}}");
        var b = JsonNode.Parse("{\"a\":{\"x\":\"s\",\"y\":[2,3]},\"b\":1}");
        Assert.Equal(ContentIdentity.OfNode(a), ContentIdentity.OfNode(b));
    }
    [Fact] public void WhitespaceDoesNotChangeIdentity()
    {
        var compact = JsonNode.Parse("{\"a\":\"x\",\"b\":[1,2]}");
        var pretty = JsonNode.Parse("{\n    \"a\": \"x\",\r\n    \"b\": [ 1,\n        2 ]\n}");
        Assert.Equal(ContentIdentity.OfNode(compact), ContentIdentity.OfNode(pretty));
    }
    [Fact] public void ValuesChangeIdentity()
    {
        Assert.NotEqual(ContentIdentity.OfNode(JsonNode.Parse("{\"a\":\"x\",\"b\":1}")), ContentIdentity.OfNode(JsonNode.Parse("{\"a\":\"x\",\"b\":2}")));
        Assert.NotEqual(ContentIdentity.OfNode(JsonNode.Parse("{\"a\":\"x\"}")), ContentIdentity.OfNode(JsonNode.Parse("{\"a\":\"y\"}")));
        Assert.NotEqual(ContentIdentity.OfNode(JsonNode.Parse("[1,2]")), ContentIdentity.OfNode(JsonNode.Parse("[2,1]"))); // arrays keep order
    }
    [Fact] public void PocoIdentityIgnoresDictionaryInsertionOrder()
    {
        var first = new { caps = new Dictionary<string, string> { ["one"] = "1", ["two"] = "2" } };
        var second = new { caps = new Dictionary<string, string> { ["two"] = "2", ["one"] = "1" } };
        Assert.Equal(ContentIdentity.Of(first), ContentIdentity.Of(second));
        Assert.NotEqual(ContentIdentity.Of(first), ContentIdentity.Of(new { caps = new Dictionary<string, string> { ["one"] = "1", ["two"] = "3" } }));
    }
    [Fact] public void KnownAnswerIsStable()
    {
        // Canonical form of {"b":1,"a":"x"} is the compact sorted text {"a":"x","b":1}.
        Assert.Equal("cdab067e9f3beb32d1252cfd63e492592fecbf591b0d08cadb24bb17f3864246", ContentIdentity.OfNode(JsonNode.Parse("{\"b\":1,\"a\":\"x\"}")));
        Assert.Equal(ContentIdentity.OfNode(JsonNode.Parse("{\"b\":1,\"a\":\"x\"}")), ContentIdentity.Of(new { a = "x", b = 1 }));
    }
}
