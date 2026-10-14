using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public class ManifestIdentityTests : TempPaths
{
    private static CapabilityManifest Manifest() // with the session/machine-volatile capability keys
    {
        var m = TestAssets.Manifest();
        m.Capabilities[CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox;
        m.Capabilities[CapabilityKeys.PresentationSourceFingerprint] = "fingerprint-1";
        m.Capabilities[CapabilityKeys.ProjectSourceRoot] = "/machine/one/source";
        return m;
    }
    [Fact] public void VolatileKeysDoNotChangeVocabularyIdentity()
    {
        var a = Manifest();
        var b = Manifest();
        b.Capabilities[CapabilityKeys.PresentationSourceFingerprint] = "fingerprint-2";
        b.Capabilities[CapabilityKeys.ProjectSourceRoot] = "/other/machine/source";
        Assert.Equal(ManifestIdentity.Of(a), ManifestIdentity.Of(b));
    }
    [Fact] public void CapabilityInsertionOrderDoesNotChangeIdentity()
    {
        var a = Manifest();
        var b = TestAssets.Manifest();
        foreach (var key in a.Capabilities.Keys.Reverse()) b.Capabilities[key] = a.Capabilities[key];
        Assert.Equal(ManifestIdentity.Of(a), ManifestIdentity.Of(b));
    }
    [Fact] public void VocabularyChangesChangeIdentity()
    {
        var a = Manifest();
        var b = Manifest();
        b.Actors[0].Clips.Add("NewClip");
        Assert.NotEqual(ManifestIdentity.Of(a), ManifestIdentity.Of(b));
        var c = Manifest();
        c.Capabilities["capture.video"] = "avpro"; // a real capability, not a volatile one
        Assert.NotEqual(ManifestIdentity.Of(a), ManifestIdentity.Of(c));
    }
    [Fact] public void InputIsNeverMutated()
    {
        var m = Manifest();
        var before = DslJson.Serialize(m);
        ManifestIdentity.Of(m);
        Assert.Equal(before, DslJson.Serialize(m));
        Assert.Equal("fingerprint-1", m.Capabilities[CapabilityKeys.PresentationSourceFingerprint]);
    }
    [Fact] public void FileLoadAndBridgeRoundTripAgree()
    {
        var m = Manifest();
        var path = TempPath("gd-manifest-", ".json");
        try
        {
            DslJson.Save(path, m);
            var fromFile = DslJson.Load<CapabilityManifest>(path);
            var bridgeStyle = DslJson.Deserialize<CapabilityManifest>(DslJson.Serialize(m)); // what the HTTP bridge client receives
            Assert.Equal(ManifestIdentity.Of(m), ManifestIdentity.Of(fromFile));
            Assert.Equal(ManifestIdentity.Of(m), ManifestIdentity.Of(bridgeStyle));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
