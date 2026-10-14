using System.Text.Json.Nodes;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

/// <summary>The job state machine (job transitions): one transition table
/// guards every state change, the DTO states the allowed actions per state, and
/// a job.json written by the pre-enum code loads with the same state strings.</summary>
public class JobFlowTests : TempPaths
{
    private static ProductionJob Job(ProductionJobState state) => new() { State = state };

    public static IEnumerable<object[]> Legal() => new[]
    {
        new object[] { ProductionJobState.Queued, ProductionJobState.Running },
        new object[] { ProductionJobState.Queued, ProductionJobState.Cancelled },
        new object[] { ProductionJobState.Queued, ProductionJobState.Interrupted }, // service restart while queued
        new object[] { ProductionJobState.Running, ProductionJobState.Verified },
        new object[] { ProductionJobState.Running, ProductionJobState.Failed },
        new object[] { ProductionJobState.Running, ProductionJobState.Interrupted },
        new object[] { ProductionJobState.Running, ProductionJobState.Cancelled },
        new object[] { ProductionJobState.Interrupted, ProductionJobState.Queued },
        new object[] { ProductionJobState.Failed, ProductionJobState.Queued },
        new object[] { ProductionJobState.Cancelled, ProductionJobState.Queued },
    };

    public static IEnumerable<object[]> Illegal() =>
        from source in Enum.GetValues<ProductionJobState>()
        from target in Enum.GetValues<ProductionJobState>()
        where !Legal().Any(row => (ProductionJobState)row[0] == source && (ProductionJobState)row[1] == target)
        select new object[] { source, target };

    [Theory]
    [MemberData(nameof(Legal))]
    public void LegalTransitionsMove(ProductionJobState from, ProductionJobState to)
    {
        var job = Job(from);
        ProductionJobFlow.Move(job, to);
        Assert.Equal(to, job.State);
    }

    [Theory]
    [MemberData(nameof(Illegal))]
    public void IllegalTransitionsThrowAndKeepTheState(ProductionJobState from, ProductionJobState to)
    {
        var job = Job(from);
        Assert.Throws<InvalidOperationException>(() => ProductionJobFlow.Move(job, to));
        Assert.Equal(from, job.State);
    }

    [Fact]
    public void EveryStateHasAnEntryInBothTables()
    {
        foreach (ProductionJobState state in Enum.GetValues<ProductionJobState>())
        {
            Assert.True(ProductionJobFlow.Transitions.ContainsKey(state));
            Assert.NotEmpty(ProductionJobFlow.Actions(state));
        }
    }

    [Theory]
    [InlineData(ProductionJobState.Queued, new[] { "cancel" })]
    [InlineData(ProductionJobState.Running, new[] { "cancel" })]
    [InlineData(ProductionJobState.Verified, new[] { "openVideo" })]
    [InlineData(ProductionJobState.Interrupted, new[] { "resume" })]
    [InlineData(ProductionJobState.Failed, new[] { "resume" })]
    [InlineData(ProductionJobState.Cancelled, new[] { "resume" })]
    public void ActionsPerState(ProductionJobState state, string[] actions)
    {
        Assert.Equal(actions, ProductionJobFlow.Actions(state));
        var view = ProductionJobFlow.View(Job(state));
        Assert.Equal(state.ToString(), view["state"]!.GetValue<string>());
        Assert.Equal(actions, view["actions"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void UndefinedStatesPermitNoUserActions() => Assert.Empty(ProductionJobFlow.Actions((ProductionJobState)999));

    [Fact]
    public void ViewSerializesTheLegacyStateStringPlusActions()
    {
        var view = ProductionJobFlow.View(new ProductionJob { Id = "job1", State = ProductionJobState.Verified, TotalShots = 2 });
        Assert.Equal("Verified", view["state"]!.GetValue<string>());
        Assert.Equal("openVideo", view["actions"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(2, view["totalShots"]!.GetValue<int>());
        Assert.Equal("job1", view["id"]!.GetValue<string>());
    }

    /// <summary>Fixture store frozen from the pre-refactor code (two job.json
    /// files in that Studio's wire shape, with synthetic role names): states stay the
    /// same strings on the wire and on disk, terminal-state files load
    /// byte-untouched, and the restart rule still interrupts a queued job.</summary>
    [Fact]
    public void LegacyJobStoreLoadsWithUnchangedStateStrings()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-jobs");
        const string queuedId = "49d9aab3d7ce4e1ba045096ed89b75cb";
        const string cancelledId = "bc3ae43aa89b4a1b899912c3caa3f865";
        Assert.Contains("\"state\": \"Queued\"", File.ReadAllText(Path.Combine(fixture, "jobs", queuedId, "job.json")));
        var cancelledBytes = File.ReadAllBytes(Path.Combine(fixture, "jobs", cancelledId, "job.json"));
        Assert.Contains("\"state\": \"Cancelled\"", System.Text.Encoding.UTF8.GetString(cancelledBytes));

        var store = TempPath("gd-legacy-jobs-");
        CopyDirectory(fixture, store);
        try
        {
            using var studio = new Studio(store);
            var interrupted = studio.Job(queuedId); // restart rule, exactly as the legacy code applied it
            Assert.Equal(ProductionJobState.Interrupted, interrupted.State);
            Assert.Equal(new[] { "resume" }, ProductionJobFlow.Actions(interrupted.State));
            Assert.Contains("\"state\": \"Interrupted\"", File.ReadAllText(Path.Combine(store, "jobs", queuedId, "job.json")));
            var cancelled = studio.Job(cancelledId);
            Assert.Equal(ProductionJobState.Cancelled, cancelled.State);
            Assert.Equal(cancelledBytes, File.ReadAllBytes(Path.Combine(store, "jobs", cancelledId, "job.json"))); // terminal states load untouched
            Assert.Equal("Cancelled", ProductionJobFlow.View(cancelled)["state"]!.GetValue<string>());
            var resumed = studio.Resume(cancelledId);
            Assert.Equal(ProductionJobState.Queued, resumed.State);
            Assert.Equal(new[] { "cancel" }, ProductionJobFlow.Actions(resumed.State));
        }
        finally { try { Directory.Delete(store, true); } catch { /* best effort */ } }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(source, target));
    }
}
