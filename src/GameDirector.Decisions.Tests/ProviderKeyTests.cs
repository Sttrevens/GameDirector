using System.Text.Json;
using GameDirector.Workbench;
using Xunit;

namespace GameDirector.Decisions.Tests;

/// <summary>API keys are write-only from the UI: an omitted (null) key keeps the
/// stored one on the exact same endpoint, is never carried to a new endpoint, an
/// explicit value replaces it, and empty endpoint + empty key disconnects.
/// Status only ever reports hasKey, never the key itself.</summary>
public class ProviderKeyTests
{
    private const string Secret = "director-secret-never-report";
    private static readonly ProviderSettings Director = new("https://api.example.com/v1/chat/completions", "m", Secret);

    private static JsonElement Status(object status) => JsonSerializer.SerializeToElement(status);

    [Fact]
    public void DirectorKeyKeepClearReplaceDisconnect()
    {
        var d = new ApiDirector();
        d.Configure(Director);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(d.Status)); // status reports hasKey, never the key
        d.Configure(Director with { Model = "m2", ApiKey = null }); // omitted key: kept on same endpoint
        var kept = Status(d.Status);
        Assert.Equal("m2", kept.GetProperty("model").GetString());
        Assert.True(kept.GetProperty("hasKey").GetBoolean());

        d.Configure(new ProviderSettings("https://other.example.com/v1/chat/completions", "m2", null)); // endpoint change clears
        Assert.False(Status(d.Status).GetProperty("hasKey").GetBoolean());
        Assert.True(Status(d.Status).GetProperty("configured").GetBoolean());

        d.Configure(new ProviderSettings("https://other.example.com/v1/chat/completions", "m2", "rotated")); // explicit key replaces
        d.Configure(new ProviderSettings("https://other.example.com/v1/chat/completions", "m2", null));
        Assert.True(Status(d.Status).GetProperty("hasKey").GetBoolean());

        d.Configure(new ProviderSettings("", "", "")); // explicit empty pair: disconnect
        Assert.False(Status(d.Status).GetProperty("configured").GetBoolean());
        Assert.False(Status(d.Status).GetProperty("hasKey").GetBoolean());
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(d.Status));
    }

    [Fact]
    public async Task DecisionsKeyKeepClearReplaceDisconnect()
    {
        await using var rig = await ServiceRig.Start(null);
        var d = rig.Decisions;
        d.ConfigureProvider(ServiceRig.Settings with { ApiKey = "jev-secret-never-report" });
        Assert.DoesNotContain("jev-secret-never-report", JsonSerializer.Serialize(d.ProviderStatus()));
        d.ConfigureProvider(ServiceRig.Settings with { ApiKey = null }); // omitted key: kept on same endpoint
        Assert.True(Status(d.ProviderStatus()).GetProperty("hasKey").GetBoolean());

        d.ConfigureProvider(ServiceRig.SettingsOtherEndpoint with { ApiKey = null }); // endpoint change clears
        Assert.False(Status(d.ProviderStatus()).GetProperty("hasKey").GetBoolean());

        d.ConfigureProvider(ServiceRig.SettingsOtherEndpoint); // explicit key configures
        d.ConfigureProvider(ServiceRig.SettingsRotatedKey with { Endpoint = ServiceRig.SettingsOtherEndpoint.Endpoint }); // replace
        d.ConfigureProvider(ServiceRig.SettingsOtherEndpoint with { ApiKey = null }); // kept after replace
        Assert.True(Status(d.ProviderStatus()).GetProperty("hasKey").GetBoolean());

        d.ConfigureProvider(new DecisionProviderSettings("", "", "")); // explicit empty pair: disconnect
        Assert.False(Status(d.ProviderStatus()).GetProperty("configured").GetBoolean());
        Assert.DoesNotContain("jev-secret-never-report", JsonSerializer.Serialize(d.ProviderStatus()));
    }
}
