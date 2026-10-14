using GameDirector.Client;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

/// <summary>Production request dedupe recomputes both sides from the stored
/// inputs; no stored hash is trusted.</summary>
public class StudioIdempotencyTests
{
    [Fact]
    public async Task SameRequestIdReusesJobAndChangedInputsConflict()
    {
        await using var rig = await ServiceRig.Start(null);
        var request = new ProductionRequest { ProjectId = "proj", RequestId = "prod-1", Film = FilmCompiler.Starter(rig.Manifest) };
        var first = await rig.Studio.Submit(request, CancellationToken.None);
        var again = await rig.Studio.Submit(request, CancellationToken.None);
        Assert.Equal(first.Id, again.Id);
        var changed = new ProductionRequest { ProjectId = "proj", RequestId = "prod-1", Film = FilmCompiler.Starter(rig.Manifest) };
        changed.Film.Title = "changed";
        await Assert.ThrowsAsync<RequestConflictFault>(() => rig.Studio.Submit(changed, CancellationToken.None));
        var stored = File.ReadAllText(Path.Combine(rig.Studio.StorePath("jobs", first.Id), "job.json"));
        Assert.DoesNotContain("requestHash", stored); // the job stores the request, not a hash of it
    }

    [Fact]
    public async Task ResumeObeysTheSameQueueBoundAsSubmit()
    {
        await using var rig = await ServiceRig.Start(null);
        var film = FilmCompiler.Starter(rig.Manifest);
        async Task<ProductionJob> Submit(string requestId) => await rig.Studio.Submit(new ProductionRequest { ProjectId = "proj", RequestId = requestId, Film = film }, CancellationToken.None);
        var cancelled = rig.Studio.Cancel((await Submit("resume-me")).Id);
        Assert.Equal(ProductionJobState.Cancelled, cancelled.State);
        for (var i = 0; i < Studio.MaxActiveJobs; i++) await Submit("queued-" + i);
        await Assert.ThrowsAsync<QueueFullFault>(() => Submit("overflow"));
        Assert.Throws<QueueFullFault>(() => rig.Studio.Resume(cancelled.Id));
        Assert.Equal(ProductionJobState.Cancelled, rig.Studio.Job(cancelled.Id).State);
    }
}
