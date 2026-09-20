using System.Diagnostics;
using GameDirector.Client;

namespace GameDirector.Workbench;

/// <summary>One way to obtain the media encoding tools on this machine.
/// Package-manager strategies delegate signature/hash verification to the OS
/// channel; download strategies carry a release-pinned SHA-256 and install into
/// the per-user media folder. Nothing here ever runs without an explicit API
/// call from a local user clicking "install".</summary>
public sealed record MediaInstallOption(string Id, string Label, string Detail, string License);
public sealed record MediaInstallRequest(string OptionId);

public static class MediaToolInstaller
{
    // Release-pinned direct downloads. Hash = the exact binary this repo's
    // media contract tests ran against; a drifted upstream fails closed.
    private sealed record DownloadSource(string Rid, string Url, string Sha256, string TargetFile);
    private static readonly DownloadSource[] Downloads =
    {
        // imageio-ffmpeg 0.6.0's own source; verified arm64 binary with libx264+libass.
        new("osx-arm64", "https://github.com/imageio/imageio-binaries/raw/master/ffmpeg/ffmpeg-osx-arm64-v7.1",
            "6d175a4743ca50256e89a8cdd731100f9cee33bd79aeea46894d209410dc6617", "ffmpeg"),
        // win-x64: pending release verification of a pinned BtbN autobuild zip; winget is the supported path until then.
    };
    private const long MaxDownloadBytes = 200L * 1024 * 1024;

    private static string? FindPackageManager(string exe)
    {
        // PATH first, then the locations package managers actually install into.
        if (Runnable(exe)) return exe;
        var candidates = exe == "brew"
            ? new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }
            : new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe + ".exe") };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool Runnable(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool StrategyAvailable(string id) => id switch
    {
        "winget-ffmpeg" => FindPackageManager("winget") != null,
        "brew-ffmpeg" => FindPackageManager("brew") != null,
        _ => Downloads.Any(d => "download-" + d.TargetFile == id && d.Rid == CurrentRid()),
    };

    public static IReadOnlyList<MediaInstallOption> Options()
    {
        var options = new List<MediaInstallOption>();
        if (OperatingSystem.IsWindows() && StrategyAvailable("winget-ffmpeg"))
            options.Add(new("winget-ffmpeg", "用 Windows 包管理器安装 FFmpeg",
                "winget install --id Gyan.FFmpeg -e（含 libx264 与 libass，由 winget 校验签名与哈希，安装在当前用户目录）",
                "FFmpeg 以 GPL 分发；libx264 为 GPL。由你的系统从你确认的源安装，GameDirector 不再分发这些二进制。"));
        if (OperatingSystem.IsMacOS() && StrategyAvailable("brew-ffmpeg"))
            options.Add(new("brew-ffmpeg", "用 Homebrew 安装 FFmpeg",
                "brew install ffmpeg（含 libx264 与 libass；同时提供 ffprobe）",
                "FFmpeg 以 GPL 分发；由你的系统从 Homebrew 源安装。"));
        foreach (var d in Downloads.Where(d => d.Rid == CurrentRid()))
            options.Add(new("download-" + d.TargetFile, "下载经过哈希钉定的 FFmpeg",
                d.Url + "（SHA-256 校验后装入当前用户的 GameDirector 媒体目录；不含 ffprobe，仍需另行提供）",
                "FFmpeg 以 GPL 分发；此构建含 libx264（GPL）与 libass（ISC）。从上游公开发布地址下载到你的机器。"));
        return options;
    }

    private static string CurrentRid() =>
        OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS()
            ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";

    private static bool Runnable(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Cheap status: resolution view + what this platform can offer.
    /// The real encoding check belongs to readiness/doctor, which run the tools.</summary>
    public static object Status()
    {
        return new
        {
            tools = MediaTools.Resolutions(),
            options = Options(),
            installFolder = MediaTools.UserMediaFolder,
        };
    }

    /// <summary>Run one offered strategy by id, then re-verify the full encoding
    /// contract and persist a receipt. Idempotent: a satisfied contract is a no-op.</summary>
    public static async Task<object> Install(Studio studio, string optionId, CancellationToken ct)
    {
        try { await MediaTools.EnsureEncoding(MediaProfile.Default, true, ct); return new { installed = false, note = "编码组件已可用，未执行安装。", tools = MediaTools.Resolutions() }; }
        catch (Exception) when (!ct.IsCancellationRequested) { /* proceed to install */ }

        var option = Options().FirstOrDefault(o => o.Id == optionId) ?? throw new ArgumentException("该平台没有这个安装方式，或该方式当前不可用。");
        var logRoot = studio.StorePath("logs"); Directory.CreateDirectory(logRoot);
        var receipt = Path.Combine(logRoot, "media-install-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
        var output = new List<string>();
        try
        {
            if (optionId == "winget-ffmpeg")
                await RunLogged(FindPackageManager("winget")!, new[] { "install", "--id", "Gyan.FFmpeg", "-e", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" }, output, ct);
            else if (optionId == "brew-ffmpeg")
                await RunLogged(FindPackageManager("brew")!, new[] { "install", "ffmpeg" }, output, ct);
            else
            {
                var d = Downloads.First(x => "download-" + x.TargetFile == optionId);
                var target = Path.Combine(MediaTools.UserMediaFolder, d.TargetFile);
                output.Add("download " + d.Url);
                await PinnedDownload.Fetch(new Uri(d.Url), d.Sha256, MaxDownloadBytes, target, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                output.Add("sha256 verified; installed to " + target);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            File.WriteAllText(receipt, string.Join("\n", output) + "\nERROR: " + ex.Message);
            throw new InvalidOperationException("安装未完成：" + ex.Message + "。完整输出保存在 " + receipt);
        }
        File.WriteAllText(receipt, string.Join("\n", output));
        MediaTools.InvalidateCache();
        await MediaTools.EnsureEncoding(MediaProfile.Default, true, ct); // whatever remains missing throws here, honestly
        return new { installed = true, receipt, tools = MediaTools.Resolutions() };
    }

    private static async Task RunLogged(string exe, string[] args, List<string> output, CancellationToken ct)
    {
        output.Add("> " + exe + " " + string.Join(" ", args));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("could not start " + exe);
        var so = p.StandardOutput.ReadToEndAsync(ct); var se = p.StandardError.ReadToEndAsync(ct);
        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(ct);
        output.Add(await so); output.Add(await se);
        if (p.ExitCode != 0) throw new InvalidOperationException(exe + " exited " + p.ExitCode + ": " + output[output.Count - 1].Trim());
    }
}
