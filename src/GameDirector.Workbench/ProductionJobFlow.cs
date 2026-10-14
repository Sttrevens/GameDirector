using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GameDirector.Workbench;

// Same strings on disk and wire as pre-enum stores/Unity clients.
[JsonConverter(typeof(JsonStringEnumConverter<ProductionJobState>))]
public enum ProductionJobState { Queued, Running, Verified, Interrupted, Failed, Cancelled }

public static class ProductionJobFlow
{
    private sealed record Rule(ProductionJobState[] Next, string[] Actions);
    // One table for both worker transitions and user actions. Resting failures
    // resume through Queued; Verified is terminal. Restart interrupts admission.
    private static readonly Dictionary<ProductionJobState, Rule> rules = new()
    {
        [ProductionJobState.Queued] = new(new[] { ProductionJobState.Running, ProductionJobState.Cancelled, ProductionJobState.Interrupted }, new[] { "cancel" }),
        [ProductionJobState.Running] = new(new[] { ProductionJobState.Verified, ProductionJobState.Failed, ProductionJobState.Interrupted, ProductionJobState.Cancelled }, new[] { "cancel" }),
        [ProductionJobState.Verified] = new(Array.Empty<ProductionJobState>(), new[] { "openVideo" }),
        [ProductionJobState.Interrupted] = new(new[] { ProductionJobState.Queued }, new[] { "resume" }),
        [ProductionJobState.Failed] = new(new[] { ProductionJobState.Queued }, new[] { "resume" }),
        [ProductionJobState.Cancelled] = new(new[] { ProductionJobState.Queued }, new[] { "resume" }),
    };
    public static readonly IReadOnlyDictionary<ProductionJobState, IReadOnlyList<ProductionJobState>> Transitions =
        new ReadOnlyDictionary<ProductionJobState, IReadOnlyList<ProductionJobState>>(rules.ToDictionary(r => r.Key, r => (IReadOnlyList<ProductionJobState>)Array.AsReadOnly(r.Value.Next)));
    public static string[] Actions(ProductionJobState state) => rules.TryGetValue(state, out var rule) ? rule.Actions.ToArray() : Array.Empty<string>();
    public static bool Allows(ProductionJobState state, string action) => rules.TryGetValue(state, out var rule) && rule.Actions.Contains(action);
    public static bool CanMove(ProductionJobState state, ProductionJobState next) => rules.TryGetValue(state, out var rule) && rule.Next.Contains(next);
    public static void Move(ProductionJob job, ProductionJobState next)
    {
        if (!CanMove(job.State, next)) throw new InvalidOperationException("Illegal job transition " + job.State + " -> " + next + ".");
        job.State = next;
    }
    // HTTP DTO only: stored journals do not gain a derived actions field.
    public static JsonObject View(ProductionJob job)
    {
        var node = JsonSerializer.SerializeToNode(job, WebOptions)!.AsObject();
        node["actions"] = new JsonArray(Actions(job.State).Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
        return node;
    }
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
}
