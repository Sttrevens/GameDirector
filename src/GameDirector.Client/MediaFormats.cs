using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameDirector.Core.Dsl;

namespace GameDirector.Client;

/// <summary>One self-contained local audio format the library accepts. The
/// demuxer name and any container-hardening args belong to the format, so the
/// importer and the normalizer can never disagree about what is supported.</summary>
public sealed class AudioFormat
{
    public AudioFormat(string extension, string demuxer, params string[] inputHardening)
    {
        Extension = extension; Demuxer = demuxer; InputHardening = inputHardening;
    }
    public string Extension { get; }
    public string Demuxer { get; }
    /// <summary>Extra ffmpeg input args that keep the container self-contained
    /// (a renamed playlist must never fetch external references).</summary>
    public IReadOnlyList<string> InputHardening { get; }
}

public static class MediaFormats
{
    private static readonly AudioFormat[] Table =
    {
        new AudioFormat(".wav", "wav"),
        new AudioFormat(".mp3", "mp3"),
        new AudioFormat(".m4a", "mov", "-enable_drefs", "0", "-use_absolute_path", "0"),
        new AudioFormat(".ogg", "ogg"),
        new AudioFormat(".flac", "flac"),
    };

    public static int MaxImportBytes => FilmLimits.MaxAudioImportBytes;
    public static IReadOnlyList<AudioFormat> Audio => Table;
    public static IReadOnlyList<string> AudioExtensions => Table.Select(f => f.Extension).ToArray();

    public static AudioFormat? FindAudio(string fileNameOrExtension)
    {
        var extension = Path.GetExtension(fileNameOrExtension ?? "").ToLowerInvariant();
        return Table.FirstOrDefault(f => f.Extension == extension);
    }

    public static AudioFormat RequireAudio(string fileNameOrExtension) =>
        FindAudio(fileNameOrExtension) ?? throw new ArgumentException(
            "Supported audio: " + string.Join(", ", Table.Select(f => f.Extension.TrimStart('.')).Select(Path.GetFileName)) + ".");
}
