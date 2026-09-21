using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDirector.Client;

/// <summary>Runtime view of the single declarative distribution manifest
/// (tools/distribution.json in the repo, staged as distribution.json at the
/// distribution root). Version comes from build metadata (the assembly
/// informational version flows from Directory.Build.props), never from a
/// copied literal. Unknown JSON members are ignored so newer manifests stay
/// readable by older runtimes.</summary>
public sealed class DistributionManifest
{
    public const string ManifestFileName = "distribution.json";
    public const string RuntimeManifestFileName = "gamedirector.runtime.json";

    /// <summary>Environment override locating a distribution root. Explicit and
    /// validated: a set-but-missing root is an error, never a silent fallback.</summary>
    public const string RootOverrideVariable = "GAMEDIRECTOR_DISTRIBUTION_ROOT";

    public int SchemaVersion { get; init; }
    public string Product { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string[] SupportedPlatforms { get; init; } = Array.Empty<string>();
    public Dictionary<string, PlatformLayout> Platforms { get; init; } = new();
    public Dictionary<string, AppLayout> Apps { get; init; } = new();
    public UnityLayout Unity { get; init; } = new();
    public MediaLayout Media { get; init; } = new();

    public sealed class PlatformLayout
    {
        public string Archive { get; init; } = "";
        public string ExecutableExtension { get; init; } = "";
        public string[] UnixExecutables { get; init; } = Array.Empty<string>();
    }

    public sealed class AppLayout
    {
        public string Folder { get; init; } = "";
        public string Executable { get; init; } = "";
    }

    public sealed class UnityLayout
    {
        public string PackageName { get; init; } = "com.gamedirector.unity";
        public string BundledRuntimeFolder { get; init; } = "Tools~/Workbench";
        public string DistributionFolder { get; init; } = "unity/com.gamedirector.unity";
    }

    public sealed class MediaLayout
    {
        public string DistributionFolder { get; init; } = "media";
        public string ManifestFile { get; init; } = "media-distributions.json";
    }

    /// <summary>The release version stamped by the build (Directory.Build.props
    /// Version flows into the assembly informational version).</summary>
    public static string ProductVersion
    {
        get
        {
            var info = typeof(DistributionManifest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var value = info?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(value)) return "0.0.0-local";
            var plus = value.IndexOf('+'); // strip SourceLink build metadata
            return plus > 0 ? value[..plus] : value;
        }
    }

    /// <summary>Current runtime identifier, matching the packaging RID names.</summary>
    public static string CurrentRid()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";
        return platform + "-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static DistributionManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<DistributionManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("distribution manifest is empty");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"unsupported distribution manifest schema {manifest.SchemaVersion}");
        if (manifest.Product != "GameDirector")
            throw new InvalidDataException($"unexpected distribution product '{manifest.Product}'");
        if (manifest.SupportedPlatforms.Length == 0 || manifest.Apps.Count == 0)
            throw new InvalidDataException("distribution manifest declares no platforms or apps");
        foreach (var rid in manifest.SupportedPlatforms)
            if (!manifest.Platforms.ContainsKey(rid))
                throw new InvalidDataException($"supported platform '{rid}' has no layout entry");
        return manifest;
    }

    public static DistributionManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Locate the distribution root: explicit override first (a set
    /// override that does not exist is an honest error), then the app folder's
    /// ancestors. Returns null when this runtime was not shipped inside a full
    /// distribution (a plain dev build); callers then give an actionable error.</summary>
    public static string? FindRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var full = Path.GetFullPath(overrideRoot);
            if (!File.Exists(Path.Combine(full, ManifestFileName)))
                throw new InvalidOperationException(
                    $"{RootOverrideVariable} points to '{full}' which has no {ManifestFileName}; fix or unset the override.");
            return full;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ManifestFileName)))
                return dir.FullName;
        return null;
    }

    public static DistributionManifest? LoadFromDistribution() =>
        FindRoot() is { } root ? Load(Path.Combine(root, ManifestFileName)) : null;

    public string ExecutableExtensionFor(string rid) =>
        Platforms.TryGetValue(rid, out var p) ? p.ExecutableExtension
        : throw new InvalidOperationException($"platform '{rid}' is not in the supported platform list ({string.Join(", ", SupportedPlatforms)})");

    /// <summary>Resolve a sibling app's executable inside this distribution.</summary>
    public string SiblingExecutable(string appKey, string distributionRoot, string rid)
    {
        if (!Apps.TryGetValue(appKey, out var app))
            throw new InvalidOperationException($"distribution manifest has no app '{appKey}'");
        return Path.Combine(distributionRoot, app.Folder, app.Executable + ExecutableExtensionFor(rid));
    }

    /// <summary>The Unity package source shipped with a full distribution.</summary>
    public string UnityPackageSource(string distributionRoot) =>
        Path.Combine(distributionRoot, Unity.DistributionFolder);
}

/// <summary>The per-app runtime manifest (gamedirector.runtime.json) stamped
/// into every published app folder and into the Unity bundle's Tools~ runtime.
/// The Unity launcher validates platform/protocol compatibility from it.</summary>
public sealed class RuntimeManifest
{
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string Rid { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string App { get; init; } = "";
    public string UnityPackageVersion { get; init; } = "";
    public string[] Executables { get; init; } = Array.Empty<string>();
    public Dictionary<string, string> Sha256 { get; init; } = new();

    /// <summary>Refuse declared paths that would escape the runtime folder:
    /// every sha256 key and executable name must be a relative path below the
    /// root. Pure string validation — no I/O — so it belongs in every gate.
    /// </summary>
    public void ValidateDeclaredPaths()
    {
        foreach (var relative in Sha256.Keys.Concat(Executables))
            ValidateDeclaredPath(relative);
    }

    internal static void ValidateDeclaredPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Any(segment => segment is ".." or ""))
            throw new InvalidDataException($"runtime manifest declares an unsafe path: '{relative}'");
    }

    /// <summary>Integrity of one declared file below root: null when the file is
    /// not declared or matches its pinned hash; otherwise the failure reason.</summary>
    public string? IntegrityFailure(string root, string relative)
    {
        ValidateDeclaredPath(relative);
        if (!Sha256.TryGetValue(relative, out var expected)) return null;
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return "declared file is missing: " + relative;
        string actual;
        using (var stream = File.OpenRead(full))
            actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        return actual == expected ? null : "declared file was modified or corrupted: " + relative;
    }

    /// <summary>First integrity failure across every declared file, or null when
    /// the whole declared tree matches. O(total runtime bytes): per-invocation
    /// gates should verify single files instead of the full tree.</summary>
    public string? IntegrityFailure(string root)
    {
        foreach (var relative in Sha256.Keys)
            if (IntegrityFailure(root, relative) is { } failure) return failure;
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static RuntimeManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<RuntimeManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("runtime manifest is empty");
        if (manifest.Product != "GameDirector")
            throw new InvalidDataException($"unexpected runtime product '{manifest.Product}'");
        if (string.IsNullOrWhiteSpace(manifest.Rid) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("runtime manifest lacks version or rid");
        return manifest;
    }

    public static RuntimeManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Compatibility failure message, or null when this runtime can
    /// serve the given rid/protocol. Protocol matches exactly; a runtime with a
    /// newer product version but the same protocol is compatible.</summary>
    public string? CompatibilityFailure(string rid, int protocolVersion)
    {
        if (Rid != rid)
            return $"runtime is built for '{Rid}' but this machine needs '{rid}'";
        if (ProtocolVersion != protocolVersion)
            return $"runtime speaks protocol {ProtocolVersion} but this host requires {protocolVersion}";
        return null;
    }
}
