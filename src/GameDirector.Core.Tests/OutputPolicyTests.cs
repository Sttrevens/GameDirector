using GameDirector.Client;
using Xunit;

public class OutputPolicyTests
{
    [Fact] public void OutputCannotEnterAnEngineOrAnAliasOfIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "game", "Assets")); Directory.CreateDirectory(Path.Combine(root, "game", "ProjectSettings"));
        try
        {
            Assert.Throws<IOException>(() => OutputPolicy.RequireOutput(Path.Combine(root, "game", "renders")));
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "game"));
            Assert.Throws<IOException>(() => OutputPolicy.RequireOutput(Path.Combine(root, "alias", "renders")));
            Directory.CreateDirectory(Path.Combine(root, "store"));
            File.CreateSymbolicLink(Path.Combine(root, "store", ".recording"), Path.Combine(root, "game", "new-file"));
            Assert.Throws<IOException>(() => TakeJobs.Lock(Path.Combine(root, "store")));
            Assert.False(File.Exists(Path.Combine(root, "game", "new-file")));
            Assert.False(Directory.Exists(Path.Combine(root, "game", "renders")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void KnownCustomSourceIsProtectedAndSiblingOutputIsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "game"));
        try
        {
            Assert.Throws<IOException>(() => OutputPolicy.RequireOutput(Path.Combine(root,"game","new"),Path.Combine(root,"game")));
            Assert.NotNull(OutputPolicy.RequireOutput(Path.Combine(root,"game-renders"),Path.Combine(root,"game")));
            Directory.CreateSymbolicLink(Path.Combine(root,"frames"),Path.Combine(root,"game"));
            Assert.Throws<IOException>(() => OutputPolicy.Child(Path.Combine(root,"frames"), "..", "escaped"));
        }
        finally { Directory.Delete(root,true); }
    }
}
