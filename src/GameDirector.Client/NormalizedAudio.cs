namespace GameDirector.Client;

// Cue time is decoded-sample time from zero, never container duration or PTS.
// Accepted formats and their container hardening live in MediaFormats; the
// sample rate lives in MediaProfile, so import and mix can never disagree.
public sealed class NormalizedAudio : IDisposable
{
    private readonly string root = OutputPolicy.RequireOutput(Path.Combine(Path.GetTempPath(), "gd-audio-" + Guid.NewGuid().ToString("N")));
    private readonly Dictionary<string, (string File, double Duration)> files = new();
    public async Task<(string File, double Duration)> Read(string input, CancellationToken ct, MediaProfile? profile = null)
    {
        profile ??= MediaProfile.Default;
        if (files.TryGetValue(input, out var saved)) return saved;
        var format = MediaFormats.RequireAudio(input);
        Directory.CreateDirectory(root);
        var output = OutputPolicy.Child(root, files.Count + ".wav");
        var args = new List<string> { "-v", "error", "-n", "-protocol_whitelist", "file", "-f", format.Demuxer };
        args.AddRange(format.InputHardening);
        var rate = profile.AudioSampleRate;
        args.AddRange(new[] { "-i", input, "-map", "0:a:0", "-vn", "-af", $"aresample={rate},asetpts=N/SR/TB", "-t", MediaTools.Number(Core.Dsl.FilmLimits.MaxOutputSeconds + 1),
            "-ac", profile.AudioChannels.ToString(), "-c:a", profile.SegmentAudioCodec, "-map_metadata", "-1", output });
        await MediaTools.Run("ffmpeg", args, ct);
        var probe = await MediaTools.Probe(output, ct);
        var audio = probe["streams"]!.AsArray().Single(s => s?["codec_type"]?.GetValue<string>() == "audio")!;
        var samples = audio["duration_ts"]!.GetValue<long>();
        var duration = samples / (double)rate;
        if (samples <= 0 || duration > Core.Dsl.FilmLimits.MaxOutputSeconds) throw new ArgumentException("Decoded audio must contain 0–" + (int)Core.Dsl.FilmLimits.MaxOutputSeconds + " seconds of samples.");
        saved = (output, duration); files.Add(input, saved); return saved;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
