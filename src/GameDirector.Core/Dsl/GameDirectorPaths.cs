using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GameDirector.Core.Dsl
{
    /// <summary>
    /// The ONE per-user data root of the product. Every host — Workbench, Unity
    /// launcher (Mono), CLI, MCP — must agree on it, or the store silently splits
    /// per launch path: Environment.SpecialFolder.LocalApplicationData resolves to
    /// ~/.local/share under Unity Mono on macOS but to ~/Library/Application
    /// Support under .NET 8, so macOS composes the root from the user profile
    /// explicitly instead of trusting that API. Pure netstandard2.1 with no
    /// dependencies: ships inside Unity and behaves identically under Mono.
    /// </summary>
    public static class GameDirectorPaths
    {
        /// <summary>Environment override selecting a different data root (must be absolute).</summary>
        public const string HomeVariable = "GAMEDIRECTOR_HOME";
        /// <summary>Descriptor a running Workbench writes into its store; locators read it.</summary>
        public const string ServiceDescriptorFile = ".service.json";
        /// <summary>Non-executable publication marker in a verified runtime mirror.</summary>
        public const string RuntimeMirrorMarkerFile = ".complete";

        /// <summary>Pure resolution, injectable for every OS combination. Order:
        /// explicit override → macOS &lt;userProfile&gt;/Library/Application Support →
        /// Windows %LocalApplicationData% → freedesktop ($XDG_DATA_HOME when fully
        /// qualified, else ~/.local/share). The product folder is appended in every
        /// case except an explicit override, which IS the root. Fully qualified is
        /// stricter than rooted: "\\foo" and "C:foo" are never accepted.</summary>
        public static string ResolveRoot(string homeOverride, bool isMac, bool isWindows, string userProfile, string localAppData, string xdgDataHome)
        {
            if (!string.IsNullOrWhiteSpace(homeOverride))
            {
                if (!Path.IsPathFullyQualified(homeOverride))
                    throw new ArgumentException(HomeVariable + " must be an absolute path: " + homeOverride);
                return Path.GetFullPath(homeOverride);
            }
            if (isMac)
                return Path.Combine(RequireRooted(userProfile, "user profile"), "Library", "Application Support", "GameDirector");
            if (isWindows)
            {
                var local = !string.IsNullOrWhiteSpace(localAppData) ? localAppData
                    : Path.Combine(RequireRooted(userProfile, "user profile"), "AppData", "Local");
                return Path.Combine(local, "GameDirector");
            }
            var data = !string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathFullyQualified(xdgDataHome)
                ? xdgDataHome
                : Path.Combine(RequireRooted(userProfile, "user profile"), ".local", "share");
            return Path.Combine(data, "GameDirector");
        }

        /// <summary>The real per-user data root of this process.</summary>
        public static string Root() => ResolveRoot(
            Environment.GetEnvironmentVariable(HomeVariable),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

        /// <summary>The Workbench store (projects, jobs, documents, service descriptor).</summary>
        public static string WorkbenchStore() => Path.Combine(Root(), "Workbench");
        /// <summary>Content-addressed mirrors of a PackageCache runtime needing chmod.</summary>
        public static string RuntimeMirror() => Path.Combine(Root(), "RuntimeMirror");
        /// <summary>Per-user bootstrap-installed media tools (ffmpeg/ffprobe).</summary>
        public static string Media() => Path.Combine(Root(), "media");
        /// <summary>Service descriptor of the Workbench running on the default store.</summary>
        public static string ServiceDescriptor() => Path.Combine(WorkbenchStore(), ServiceDescriptorFile);

        /// <summary>Where Unity Mono launchers put the Workbench store on macOS before
        /// the root was unified (LocalApplicationData resolved to ~/.local/share).</summary>
        public static string LegacyMacWorkbenchStore(string userProfile) =>
            Path.Combine(userProfile, ".local", "share", "GameDirector", "Workbench");

        private static string RequireRooted(string path, string what)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Cannot locate the per-user data root: " + what + " is not an absolute path.");
            return path;
        }
    }

    /// <summary>
    /// Workbench wire identity, stated once: the descriptor writer, the /api/health
    /// endpoint, the Unity launcher, the MCP locator and every test derive product
    /// name and protocol version from these constants instead of restating literals.
    /// </summary>
    public static class WorkbenchService
    {
        public const string Product = "GameDirector";
        public const int ProtocolVersion = 1;
    }
}
