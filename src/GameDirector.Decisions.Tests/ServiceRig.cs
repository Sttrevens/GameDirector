using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;
using GameDirector.Workbench;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameDirector.Decisions.Tests;

/// <summary>A real Studio on a temp store plus a loopback fake engine bridge
/// serving the manifest, so service tests exercise catalog freshness, document
/// identity and compiler validation exactly as production does.</summary>
public sealed class ServiceRig : IAsyncDisposable
{
    public string Root = "";
    public string Source = "";
    public Studio Studio = null!;
    public DecisionService Decisions = null!;
    public CapabilityManifest Manifest = null!;
    public string ManifestHash = "";
    private WebApplication? bridge;

    public static readonly DecisionProviderSettings Settings = new("http://127.0.0.1:9/decisions", "typesafe/jev-1.13", "key");
    /// <summary>Same endpoint with a rotated memory-only key: identical decision
    /// identity. An endpoint change is NOT identical.</summary>
    public static readonly DecisionProviderSettings SettingsRotatedKey = new("http://127.0.0.1:9/decisions", "typesafe/jev-1.13", "rotated-key");
    public static readonly DecisionProviderSettings SettingsOtherEndpoint = new("http://127.0.0.1:9/other-decisions", "typesafe/jev-1.13", "key");

    public static async Task<ServiceRig> Start(IDecisionProvider? provider)
    {
        var rig = new ServiceRig();
        rig.Root = Path.Combine(Path.GetTempPath(), "gd-test-" + Guid.NewGuid().ToString("N"));
        rig.Source = Path.Combine(Path.GetTempPath(), "gd-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rig.Root);
        Directory.CreateDirectory(rig.Source);
        rig.Manifest = TestRig.Manifest(rig.Source);
        rig.ManifestHash = FilmCompiler.Hash(rig.Manifest);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var manifest = rig.Manifest;
        app.MapGet("/" + BridgeRoutes.Manifest, () => Results.Json(manifest, DslJson.Options));
        await app.StartAsync();
        rig.bridge = app;
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();

        rig.Studio = new Studio(rig.Root);
        rig.Studio.Register(new StudioProject { Id = "proj", Name = "Test", Engine = "Unity", Endpoint = address });
        rig.Decisions = new DecisionService(rig.Studio, _ => provider ?? throw new InvalidOperationException("no provider in test"));
        return rig;
    }

    public string CatalogDir() => Studio.StorePath("catalog", "proj");

    /// <summary>Explicitly configure per-task policies. Policies ship
    /// uncalibrated (null thresholds), so tests that expect a selected outcome
    /// must state their bar explicitly — exactly like a real deployment.</summary>
    public void Calibrate(Action<DecisionPolicies> configure)
    {
        var p = Decisions.GetPolicies();
        configure(p);
        Decisions.ConfigurePolicies(p);
    }

    public string[] GeneratedDecisionIds(string task)
    {
        var dir = Studio.StorePath("decisions", "proj", task);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "gen-*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).ToArray() : Array.Empty<string>();
    }

    public void WriteCatalog(PerformanceCatalog catalog)
    {
        Directory.CreateDirectory(CatalogDir());
        DslJson.SaveAtomic(Path.Combine(CatalogDir(), "performance-catalog.json"), catalog);
    }

    public MediaAsset WriteMedia(string name, string? description)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).Take(64).ToArray();
        var directory = Studio.StorePath("media", "proj");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, name);
        File.WriteAllBytes(temp, bytes);
        var id = MediaTools.Hash(temp);
        var final = Path.Combine(directory, id + ".wav");
        File.Move(temp, final, true);
        var asset = new MediaAsset(id, name, ".wav", 3.0, bytes.Length) { Description = description };
        DslJson.SaveAtomic(Path.Combine(directory, id + ".json"), asset);
        return asset;
    }

    public bool RecordExists(string task, string requestId)
    {
        var path = Path.Combine(Studio.StorePath("decisions", "proj", task), requestId + ".json");
        return File.Exists(path);
    }

    public async ValueTask DisposeAsync()
    {
        if (bridge != null) await bridge.StopAsync();
        Studio.Dispose();
        try { Directory.Delete(Root, true); Directory.Delete(Source, true); } catch { /* best effort */ }
    }
}
