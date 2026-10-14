using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class MeasureContractTests
{
    // Catches an inspection stamp accidentally becoming vocabulary or machine-local.
    [Fact] public void MeasureStampIsPublicInspectionAndDoesNotChangeManifestIdentity()
    {
        var row=CapabilitySpecs.Find("measure");Assert.NotNull(row);
        Assert.Equal("enum",row.Kind);Assert.Equal("absent",row.Undeclared);Assert.Equal("adapter-or-engine",row.Declarer);
        Assert.Equal("inspection",row.Identity);Assert.Equal("public",row.Exposure);
        Assert.Equal("measure-v1",Assert.Single(row.Values).Value);
        var manifest=TestAssets.Manifest();var before=ManifestIdentity.Of(manifest);
        manifest.Capabilities["measure"]="measure-v1";Assert.Equal(before,ManifestIdentity.Of(manifest));
        Assert.DoesNotContain("measure",CapabilityKeys.MachineLocal);
    }
    // Catches fields missing from the engine-facing payload and the wrong pitch/roll meaning.
    [Fact] public void MeasureDeclaresRawFieldsRoleReferenceAndIndependentHorizonSamples()
    {
        var graph=JsonNode.Parse(DslJson.Serialize(EngineContract.Current))!;var spec=graph["measure"];Assert.NotNull(spec);
        Assert.Equal(new[]{"frame","time","camera.position[3]","camera.right[3]","camera.up[3]","camera.forward[3]","camera.fov","camera.aspect","composition"},
            spec!["fields"]!.AsArray().Select(n=>n!.GetValue<string>()));
        Assert.Equal("review.roles",spec["composition"]!.GetValue<string>());
        Assert.Equal("horizon-roll-v1",spec["horizonRoll"]!["id"]!.GetValue<string>());
        Assert.False(graph["take"]!["requestDefaults"]!["measure"]!.GetValue<bool>());
        var rows=spec["samples"]!.AsArray();Assert.Equal(4,rows.Count);
        foreach(var (row,want) in rows.Take(3).Zip(new[]{10d,8.64916510528758,-4.434702030943713}))
            Assert.InRange(Math.Abs(row!["horizonRoll"]!.GetValue<double>()-want),0,1e-9);
    }
    // Catches any non-additive drift in the frozen 1.1 graph.
    [Fact] public void Contract12OnlyAddsApprovedMembersToFrozen11()
    {
        var graph=JsonNode.Parse(DslJson.Serialize(EngineContract.Current))!;Assert.Equal(2,graph["version"]!["minor"]!.GetValue<int>());
        Strip12(graph);var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!File.Exists(Path.Combine(dir.FullName,"GameDirector.sln")))dir=dir.Parent;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"docs/contract/engine-contract-1.1.json"))),graph));
    }
    internal static void Strip12(JsonNode graph)
    {
        Assert.True(graph.AsObject().Remove("measure"));var rows=graph["capabilities"]!.AsArray();
        var measure=Assert.Single(rows.Where(r=>r!["key"]!.GetValue<string>()=="measure"));rows.Remove(measure);
        Assert.True(graph["take"]!["requestDefaults"]!.AsObject().Remove("measure"));graph["version"]!["minor"]=1;
    }
}
