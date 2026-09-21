using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace GameDirector.Client;

/// <summary>How one media tool (ffmpeg/ffprobe) was found, for diagnostics.
/// Resolution order is fixed: explicit environment override, then the runtime
/// layout bundled next to the app, then well-known package-manager install
/// locations, then the system PATH.</summary>
public sealed record MediaToolResolution(string Name, string Path, string Source, bool Broken = false);

public static class MediaTools
{
    private static readonly Dictionary<string, MediaToolResolution> resolved = new();

    /// <summary>Install locations a first-time user's package manager would
    /// have used, probed before bare PATH resolution so a minimal service
    /// environment (no login shell PATH) still finds a system install.
    /// Includes the per-user WinGet shim folder, since a Unity-launched
    /// process does not inherit a PATH that changed after Unity started.</summary>
    private static IEnumerable<string> WellKnownFolders()
    {
        if (OperatingSystem.IsMacOS()) { yield return "/opt/homebrew/bin"; yield return "/usr/local/bin"; }
        else if (OperatingSystem.IsLinux()) { yield return "/usr/local/bin"; yield return "/usr/bin"; }
        else if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
        }
    }

    /// <summary>Forget cached resolutions (after an install changed the machine).</summary>
    public static void InvalidateCache() { lock (resolved) resolved.Clear(); }

    /// <summary>Per-user folder receiving bootstrap-installed tools. Writable
    /// where the packaged app folder is not (PackageCache), survives upgrades.</summary>
    public static string UserMediaFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDirector", "media");

    /// <summary>Resolve one tool without assuming anything about the install.</summary>
    public static MediaToolResolution Resolve(string tool)
    {
        lock (resolved) if (resolved.TryGetValue(tool, out var saved)) return saved;
        var variable = "GAMEDIRECTOR_" + tool.ToUpperInvariant();
        var file = tool + (OperatingSystem.IsWindows() ? ".exe" : "");
        MediaToolResolution result;
        var overridePath = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // An explicit override is a promise: a missing target is reported
            // broken rather than silently falling back to another ffmpeg.
            result = File.Exists(overridePath)
                ? new MediaToolResolution(tool, overridePath, variable)
                : new MediaToolResolution(tool, overridePath, variable + " (not found)", Broken: true);
        }
        else
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "media", file),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "media", file)),
                Path.Combine(UserMediaFolder, file),
            }.Concat(WellKnownFolders().Select(folder => Path.Combine(folder, file)));
            var found = candidates.FirstOrDefault(File.Exists);
            result = found != null
                ? new MediaToolResolution(tool, found, found.StartsWith(UserMediaFolder, StringComparison.OrdinalIgnoreCase) ? "user bootstrap"
                    : found.Contains("media" + Path.DirectorySeparatorChar) ? "bundled runtime" : "system install")
                : new MediaToolResolution(tool, tool, "PATH");
        }
        lock (resolved) resolved[tool] = result;
        return result;
    }

    /// <summary>Diagnostic view of every tool the pipeline runs.</summary>
    public static IReadOnlyList<MediaToolResolution> Resolutions() =>
        new[] { "ffmpeg", "ffprobe" }.Select(Resolve).ToArray();

    public static string FilterPath(string path) => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "'\\''");
    public static string Number(double n) => n.ToString("0.########",CultureInfo.InvariantCulture);
    public static string Hash(string path) { using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }

    public static async Task<string> Run(string executable,IEnumerable<string> args,CancellationToken ct=default)
    {
        if(executable is "ffmpeg" or "ffprobe") executable = Resolve(executable).Path;
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var a in args)info.ArgumentList.Add(a);
        using var p=Process.Start(info)??throw new InvalidOperationException("could not start "+executable);
        var error=p.StandardError.ReadToEndAsync(ct);var output=p.StandardOutput.ReadToEndAsync(ct);
        try{await p.WaitForExitAsync(ct);}catch{if(!p.HasExited)p.Kill(true);throw;}
        var stderr=await error;var stdout=await output;
        if(p.ExitCode!=0)throw new InvalidOperationException(executable+" failed: "+stderr);
        return stdout;
    }

    public static Task EnsureEncoding(bool subtitles,CancellationToken ct=default) =>
        EnsureEncoding(MediaProfile.Default,subtitles,ct);

    /// <summary>Fail fast unless the resolved tools can honor this profile:
    /// the configured encoder, the filters burning captions needs, ffprobe, and
    /// the pinned subtitle font actually present and unmodified.</summary>
    public static Task EnsureEncoding(MediaProfile profile,bool subtitles,CancellationToken ct=default)
    {
        foreach(var tool in new[]{"ffmpeg","ffprobe"})
        {
            var resolution=Resolve(tool);
            if(resolution.Broken)
                throw new InvalidOperationException(resolution.Source.Split(' ')[0]+" points to '"+resolution.Path+"' which does not exist; fix the path or unset the variable.");
        }
        return EnsureEncodingCore(profile,subtitles,Resolve("ffmpeg").Path,Resolve("ffprobe").Path,
            "set GAMEDIRECTOR_FFMPEG to a compatible executable",ct);
    }

    /// <summary>Full contract check against two exact executables — used to prove
    /// staged binaries BEFORE activation. No resolution and no environment
    /// overrides: the given paths are the only tools consulted.</summary>
    public static Task EnsureEncoding(MediaProfile profile,bool subtitles,string ffmpegPath,string ffprobePath,CancellationToken ct=default)
    {
        foreach(var(tool,path)in new[]{("ffmpeg",ffmpegPath),("ffprobe",ffprobePath)})
            if(!File.Exists(path))
                throw new InvalidOperationException("staged "+tool+" is missing: "+path);
        return EnsureEncodingCore(profile,subtitles,Path.GetFullPath(ffmpegPath),Path.GetFullPath(ffprobePath),
            "the staged ffmpeg is not a compatible executable",ct);
    }

    private static async Task EnsureEncodingCore(MediaProfile profile,bool subtitles,string ffmpegPath,string ffprobePath,string encoderHint,CancellationToken ct)
    {
        var encoders=await Run(ffmpegPath,new[]{"-hide_banner","-encoders"},ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(encoders,@"(?m)^\s*\S+\s+"+System.Text.RegularExpressions.Regex.Escape(profile.VideoCodec)+@"\s"))
            throw new InvalidOperationException("FFmpeg requires the "+profile.VideoCodec+" encoder; "+encoderHint);
        var filters=profile.RequiredFilters(subtitles);
        if(filters.Count>0) {
            var font=profile.SubtitleFont.Locate();
            if (!File.Exists(font))
                throw new InvalidOperationException("Subtitle font is missing. Reinstall the complete GameDirector runtime, including its Fonts directory.");
            if (Hash(font)!=profile.SubtitleFont.Sha256)
                throw new InvalidOperationException("Subtitle font differs from the pinned delivery. Reinstall the unmodified GameDirector runtime fonts.");
            var available=await Run(ffmpegPath,new[]{"-hide_banner","-filters"},ct);
            foreach(var filter in filters)
                if(!System.Text.RegularExpressions.Regex.IsMatch(available,@"(?m)^\s*\S+\s+"+System.Text.RegularExpressions.Regex.Escape(filter)+@"\s"))
                    throw new InvalidOperationException("This edit requires FFmpeg with "+filter+" support; "+encoderHint);
        }
        await Run(ffprobePath,new[]{"-version"},ct);
    }

    public static async Task<JsonObject> Probe(string file,CancellationToken ct=default) =>
        (JsonNode.Parse(await Run("ffprobe",new[]{"-v","error","-count_frames","-show_streams","-show_format","-of","json",file},ct)) as JsonObject)!;
    public static async Task Verify(string file,int frames,int fps,int width,int height,CancellationToken ct=default)
    {
        var probe=await Probe(file,ct);
        var stream=probe["streams"]!.AsArray().First(x=>x?["codec_type"]?.GetValue<string>()=="video")!;
        if(stream["width"]!.GetValue<int>()!=width || stream["height"]!.GetValue<int>()!=height || int.Parse(stream["nb_read_frames"]!.GetValue<string>())!=frames)
            throw new InvalidOperationException("encoded dimensions or frame count differ from the take contract");
        var rate=stream["avg_frame_rate"]!.GetValue<string>().Split('/');
        if(Math.Abs(double.Parse(rate[0],CultureInfo.InvariantCulture)/double.Parse(rate[1],CultureInfo.InvariantCulture)-fps)>.001)
            throw new InvalidOperationException("encoded frame rate mismatch");
        await Run("ffmpeg",new[]{"-v","error","-i",file,"-f","null","-"},ct);
    }
}
