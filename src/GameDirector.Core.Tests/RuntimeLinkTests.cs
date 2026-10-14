using GameDirector.Client;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public sealed class RuntimeLinkTests : TempPaths
{
    [Theory]
    [InlineData(0xA000000Cu, true)] // symlink
    [InlineData(0xA0000003u, true)] // junction / volume mount point
    [InlineData(0xA0001027u, true)] // another name-surrogate link
    [InlineData(0x9000001Au, false)] // OneDrive cloud
    [InlineData(0x9000101Au, false)] // cloud variant
    [InlineData(0x80000015u, false)] // placeholder
    [InlineData(0u, false)]
    public void WindowsReparseTagsDistinguishLinksFromPlaceholders(uint tag, bool expected)
    {
        var method = typeof(RuntimeFilePolicy).GetMethod("IsWindowsLinkTag");
        Assert.NotNull(method);
        Assert.Equal(expected, (bool)method.Invoke(null, new object[] { tag })!);
    }

    [Fact]
    public void LinkedRuntimeRootIsRefused()
    {
        var root = TempPath("gd-real-root-"); Directory.CreateDirectory(root);
        var link = TempPath("gd-root-link-"); TestDirectoryLinks.Create(link, root);
        var manifest = new RuntimeManifest();
        Assert.Contains("link", manifest.IntegrityFailure(link));
    }
}
