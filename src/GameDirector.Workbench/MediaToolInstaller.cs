using GameDirector.Client;

namespace GameDirector.Workbench;

/// <summary>One way to obtain the media encoding tools on this machine.
/// All strategy data is declarative (media-distributions.json, shipped with
/// the app); the install unit is the complete ffmpeg+ffprobe capability and
/// partial bundles are never advertised as ready. Package-manager strategies
/// delegate signature/hash verification to the OS channel; user-supplied
/// offline bundles are hash-verified file by file. Nothing here ever runs
/// without an explicit API call from a local user clicking "install".</summary>
public sealed record MediaInstallOption(string Id, string Label, string Detail, string License, string Kind);
public sealed record MediaInstallRequest(string OptionId, string? DescriptorPath = null);

public static class MediaToolInstaller
{
    /// <summary>Where the Workbench API looks for a user-supplied offline bundle
    /// descriptor (the HTTP surface only carries an option id). The user-bundle
    /// option's detail text points here; gd's CLI accepts an explicit path.</summary>
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
                ? o with { Detail = o.Detail + " 输入套件描述文件的完整路径。" }
                : o)
            .ToArray();
    }

    /// <summary>Run one offered strategy by id, then re-verify the full encoding
    /// contract and persist a receipt. Idempotent: a satisfied contract is a no-op.</summary>
    public static async Task<object> Install(Studio studio, string optionId, CancellationToken ct, string? descriptorPath = null)
    {
        var manifest = Manifest;
        var receiptFolder = studio.StorePath("logs");
        string? descriptor = null;
        if (optionId == manifest.UserBundle.Id)
        {
            descriptor = string.IsNullOrWhiteSpace(descriptorPath) ? UserBundleDescriptorPath(studio) : Path.GetFullPath(descriptorPath);
            if (!File.Exists(descriptor))
                throw new ArgumentException(
                    "离线套件安装需要一个核验描述文件：请将你准备好的 bundle descriptor JSON 保存为 " + descriptor +
                    "（描述文件必须为 ffmpeg 与 ffprobe 同时给出路径和 SHA-256），然后重试；或改用 gd media install --descriptor <path>。");
        }
        var receipt = await MediaInstaller.Install(manifest, Rid, optionId, descriptor, receiptFolder, ct);
        if (!receipt.Installed)
            return new { installed = false, note = receipt.Note ?? "编码组件已可用，未执行安装。", tools = receipt.Tools };
        return new { installed = true, receipt = receipt.ReceiptPath, tools = receipt.Tools };
    }
}
