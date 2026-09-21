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
            TestDirectoryLinks.Create(Path.Combine(root, "alias"), Path.Combine(root, "game"));
            Assert.Throws<IOException>(() => OutputPolicy.RequireOutput(Path.Combine(root, "alias", "renders")));
            Directory.CreateDirectory(Path.Combine(root, "store"));
            if (OperatingSystem.IsWindows())
                TestDirectoryLinks.Create(Path.Combine(root, "store", ".recording"), Path.Combine(root, "game"));
            else
                File.CreateSymbolicLink(Path.Combine(root, "store", ".recording"), Path.Combine(root, "game", "new-file"));
            Assert.Throws<IOException>(() => TakeJobs.Lock(Path.Combine(root, "store")));
            Assert.False(File.Exists(Path.Combine(root, "game", "new-file")));
            Assert.False(Directory.Exists(Path.Combine(root, "game", "renders")));
        }
        finally
        {
            // Remove aliases before their targets; Windows junction removal
            // cannot be delegated to recursive deletion after target removal.
            foreach (var alias in new[] { Path.Combine(root, "store", ".recording"), Path.Combine(root, "alias") })
                if (new DirectoryInfo(alias).LinkTarget != null)
                {
                    if (Directory.Exists(alias)) Directory.Delete(alias); else File.Delete(alias);
                }
            Directory.Delete(root, true);
        }
    }
    [Fact] public void KnownCustomSourceIsProtectedAndSiblingOutputIsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "game"));
        try
        {
            Assert.Throws<IOException>(() => OutputPolicy.RequireOutput(Path.Combine(root,"game","new"),Path.Combine(root,"game")));
            Assert.NotNull(OutputPolicy.RequireOutput(Path.Combine(root,"game-renders"),Path.Combine(root,"game")));
            TestDirectoryLinks.Create(Path.Combine(root,"frames"),Path.Combine(root,"game"));
            Assert.Throws<IOException>(() => OutputPolicy.Child(Path.Combine(root,"frames"), "..", "escaped"));
        }
        finally
        {
            var alias = Path.Combine(root, "frames");
            if (new DirectoryInfo(alias).LinkTarget != null) Directory.Delete(alias);
            Directory.Delete(root,true);
        }
    }
}
