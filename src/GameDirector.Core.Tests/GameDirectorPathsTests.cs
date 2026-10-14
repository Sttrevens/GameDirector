using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

/// <summary>The per-user data root is a pure function of its inputs, so every OS
/// mapping is asserted here instead of being discovered on a user's machine.
/// Path.IsPathFullyQualified semantics differ by host OS (unix-style rooted paths
/// are not fully qualified on Windows, and vice versa), so the table mirrors each
/// case per host style — an inapplicable row simply is not emitted on that host.</summary>
public class GameDirectorPathsTests
{
    public static IEnumerable<object?[]> RootCases()
    {
        // Windows branch with LocalApplicationData set: no profile involved, so this
        // row means the same on every host.
        yield return Case(null, mac: false, win: true, @"C:\Users\alice", @"C:\Users\alice\AppData\Local", null,
            Path.Combine(@"C:\Users\alice\AppData\Local", "GameDirector"));
        if (!OperatingSystem.IsWindows())
        {
            // Unix host: slash paths are fully qualified.
            // macOS composes from the user profile explicitly: LocalApplicationData
            // differs between Unity Mono and .NET 8 and is never consulted here.
            yield return Case(null, mac: true, win: false, "/Users/alice", "/mono-would-say-local-share", null,
                Path.Combine("/Users/alice", "Library", "Application Support", "GameDirector"));
            // Windows branch, LocalApplicationData empty: profile fallback.
            yield return Case(null, mac: false, win: true, "/Users/alice", "", null,
                Path.Combine("/Users/alice", "AppData", "Local", "GameDirector"));
            // freedesktop: fully-qualified XDG_DATA_HOME wins; relative, drive-relative
            // or absent falls back to ~/.local/share ("C:foo"/"\foo" are not fully qualified).
            yield return Case(null, mac: false, win: false, "/home/alice", "/ignored", null,
                Path.Combine("/home/alice", ".local", "share", "GameDirector"));
            yield return Case(null, mac: false, win: false, "/home/alice", "/ignored", "/data",
                Path.Combine("/data", "GameDirector"));
            yield return Case(null, mac: false, win: false, "/home/alice", "/ignored", "relative-xdg",
                Path.Combine("/home/alice", ".local", "share", "GameDirector"));
            yield return Case(null, mac: false, win: false, "/home/alice", "/ignored", @"C:foo",
                Path.Combine("/home/alice", ".local", "share", "GameDirector"));
            yield return Case(null, mac: false, win: false, "/home/alice", "/ignored", @"\foo",
                Path.Combine("/home/alice", ".local", "share", "GameDirector"));
            // An explicit override IS the root: the product folder is not appended.
            yield return Case("/elsewhere", mac: true, win: false, "/Users/alice", "", null, null); // null = GetFullPath(override)
            yield return Case("/elsewhere", mac: false, win: false, "/home/alice", "", "/data", null);
        }
        else
        {
            // Windows host: drive-letter paths are fully qualified. Same mappings,
            // mirrored in the host's path style.
            yield return Case(null, mac: true, win: false, @"C:\Users\alice", @"D:\ignored", null,
                Path.Combine(@"C:\Users\alice", "Library", "Application Support", "GameDirector"));
            yield return Case(null, mac: false, win: true, @"C:\Users\alice", "", null,
                Path.Combine(@"C:\Users\alice", "AppData", "Local", "GameDirector"));
            yield return Case(null, mac: false, win: false, @"C:\Users\alice", @"D:\ignored", null,
                Path.Combine(@"C:\Users\alice", ".local", "share", "GameDirector"));
            yield return Case(null, mac: false, win: false, @"C:\Users\alice", @"D:\ignored", @"C:\data",
                Path.Combine(@"C:\data", "GameDirector"));
            yield return Case(null, mac: false, win: false, @"C:\Users\alice", @"D:\ignored", "relative-xdg",
                Path.Combine(@"C:\Users\alice", ".local", "share", "GameDirector"));
            // "\foo" and "C:foo" are rooted but NOT fully qualified on Windows
            // (drive-less / drive-relative): XDG ignores them, the override rejects them.
            yield return Case(null, mac: false, win: false, @"C:\Users\alice", @"D:\ignored", @"C:foo",
                Path.Combine(@"C:\Users\alice", ".local", "share", "GameDirector"));
            yield return Case(null, mac: false, win: false, @"C:\Users\alice", @"D:\ignored", @"\foo",
                Path.Combine(@"C:\Users\alice", ".local", "share", "GameDirector"));
            yield return Case(@"C:\elsewhere", mac: true, win: false, @"C:\Users\alice", "", null, null);
            yield return Case(@"C:\elsewhere", mac: false, win: false, @"C:\Users\alice", "", @"C:\data", null);
        }
    }
    private static object?[] Case(string? homeOverride, bool mac, bool win, string profile, string local, string? xdg, string? expected) =>
        new object?[] { homeOverride, mac, win, profile, local, xdg, expected };

    [Theory]
    [MemberData(nameof(RootCases))]
    public void ResolveRootMapsEveryOsAndOverride(string? homeOverride, bool mac, bool win, string profile, string local, string? xdg, string? expected)
    {
        var resolved = GameDirectorPaths.ResolveRoot(homeOverride, mac, win, profile, local, xdg);
        Assert.Equal(expected ?? Path.GetFullPath(homeOverride!), resolved);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("Library/Application Support")]
    [InlineData(@"\foo")]  // rooted but drive-less on Windows; plain relative on unix
    [InlineData(@"C:foo")] // drive-relative on Windows; plain relative on unix
    public void NotFullyQualifiedOverrideIsRejected(string homeOverride) =>
        // The override is validated before any OS branch, so the thrown rows hold on every host.
        Assert.Throws<ArgumentException>(() => GameDirectorPaths.ResolveRoot(homeOverride, true, false, "/Users/alice", "", null));

    [Fact]
    public void DerivedFoldersHangOffTheSingleRoot()
    {
        Assert.Equal(Path.Combine(GameDirectorPaths.Root(), "Workbench"), GameDirectorPaths.WorkbenchStore());
        Assert.Equal(Path.Combine(GameDirectorPaths.Root(), "RuntimeMirror"), GameDirectorPaths.RuntimeMirror());
        Assert.Equal(Path.Combine(GameDirectorPaths.Root(), "media"), GameDirectorPaths.Media());
        Assert.Equal(Path.Combine(GameDirectorPaths.WorkbenchStore(), GameDirectorPaths.ServiceDescriptorFile), GameDirectorPaths.ServiceDescriptor());
    }

    [Fact]
    public void LegacyMacStoreIsTheOldMonoLocalApplicationDataLayout() =>
        Assert.Equal(Path.Combine("/Users/alice", ".local", "share", "GameDirector", "Workbench"),
            GameDirectorPaths.LegacyMacWorkbenchStore("/Users/alice"));

    [Fact]
    public void RootResolvesOnThisMachine() =>
        Assert.True(Path.IsPathFullyQualified(GameDirectorPaths.Root()));
}
