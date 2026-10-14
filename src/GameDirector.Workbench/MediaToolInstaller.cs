using GameDirector.Client;

namespace GameDirector.Workbench;

/// <summary>One way to obtain the media encoding tools on this machine.
/// All strategy data is declarative (media-distributions.json, shipped with
/// the app); the install unit is the complete ffmpeg+ffprobe capability and
/// partial bundles are never advertised as ready. Package-manager strategies
/// delegate signature/hash verification to the OS channel; user-supplied
/// offline bundles are hash-verified file by file. Nothing here ever runs
/// without an explicit authenticated API call from the owning user. An offline
/// descriptor authorizes executing the supplied binaries; its hashes prove only
/// integrity, not that a bundle is trustworthy.</summary>
public sealed record MediaInstallOption(string Id, string Label, string Detail, string License, string Kind)
{
    public string LabelCode => "install." + Id + ".label";
    public string DetailCode => "install." + Id + ".detail";
    public string LicenseCode => "install." + Id + ".license";
}
public sealed record MediaInstallRequest(string OptionId, string? DescriptorPath = null);

public static class MediaToolInstaller
{
    /// <summary>Where the Workbench API looks for a user-supplied offline bundle
    /// descriptor when the authenticated HTTP caller omits DescriptorPath.
    /// Both the API and gd's CLI accept an explicit regular-file path.</summary>
    public static string UserBundleDescriptorPath(Studio studio) => studio.StorePath("media-bundle.json");

    private static MediaDistributions Manifest => MediaDistributions.LoadBundled();
    private static string Rid => DistributionManifest.CurrentRid();

    /// <summary>Cheap status: resolution view + what this platform can offer.
    /// The real encoding check belongs to readiness/doctor, which run the tools.</summary>
    public static object Status()
    {
        return new
        {
            tools = MediaTools.Resolutions(),
            unit = Manifest.Unit.Tools,
            options = Options(),
            installFolder = MediaTools.UserMediaFolder,
        };
    }

    public static IReadOnlyList<MediaInstallOption> Options()
    {
        var manifest = Manifest;
        var options = MediaInstaller.Options(manifest, Rid)
            .Select(o => new MediaInstallOption(o.Id, o.Label, o.Detail, o.License, o.Kind))
            .ToList();
        // Keep the conventional location for existing callers; new clients can
        // supply the descriptor's path explicitly.
        return options
            .Select(o => o.Id == manifest.UserBundle.Id
                ? o with { Detail = o.Detail + " Enter the full descriptor path." }
                : o)
            .ToArray();
    }

    /// <summary>Run one offered strategy by id, then re-verify the full encoding
    /// contract and persist a receipt. Idempotent: a satisfied contract is a no-op.</summary>
    public static async Task<MediaInstaller.Receipt> Install(Studio studio, string optionId, CancellationToken ct, string? descriptorPath = null, Action<string>? output = null)
    {
        var manifest = Manifest;
        var receiptFolder = studio.StorePath("logs");
        string? descriptor = null;
        if (optionId == manifest.UserBundle.Id)
        {
            descriptor = string.IsNullOrWhiteSpace(descriptorPath) ? UserBundleDescriptorPath(studio) : Path.GetFullPath(descriptorPath);
            if (!File.Exists(descriptor))
                throw new ArgumentException(
                    "Offline installation needs a verified bundle descriptor JSON at " + descriptor +
                    ". It must declare paths and SHA-256 hashes for both ffmpeg and ffprobe. Save it there and retry, or use gd media install --descriptor <path>.");
            descriptor = MediaBundleDescriptor.RequireFile(descriptor);
        }
        return await MediaInstaller.Install(manifest, Rid, optionId, descriptor, receiptFolder, ct, output);
    }
}
