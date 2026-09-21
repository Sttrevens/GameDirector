using System.Text.RegularExpressions;

namespace GameDirector.Decisions;

/// <summary>Provider payloads must describe intent, never the local machine.
/// Free-text entering a decision request passes through Scrub: known local roots
/// are replaced first, then remaining path-like fragments (drive-letter paths,
/// UNC shares, absolute POSIX paths and file URIs) are redacted. FindPathLike is the final guard
/// on the serialized payload.</summary>
public static class LocalPathScrubber
{
    // A drive letter is a single letter NOT preceded by another letter, so URL
    // schemes ("https://…") are never mistaken for drives.
    private static readonly Regex DrivePath = new(@"(?<![A-Za-z])[A-Za-z]:[\\/][^\s""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled);
    private static readonly Regex HomePath = new(@"/(?:home|Users|root)/[^\s""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex FileUri = new(@"file://[^\s""'<>|]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AbsolutePosixPath = new(@"(?<![A-Za-z0-9_:/.])/(?!/)[^\s""'<>|]+", RegexOptions.Compiled);
    private const string Redacted = "[local-path]";

    public static string Scrub(string? text, params string?[] knownRoots)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = text;
        foreach (var root in knownRoots)
            if (!string.IsNullOrWhiteSpace(root))
            {
                var prefix = root.TrimEnd('\\', '/');
                if (prefix.Length == 0) continue;
                result = result.Replace(prefix.Replace("\\", "\\\\"), Redacted, StringComparison.OrdinalIgnoreCase)
                               .Replace(prefix, Redacted, StringComparison.OrdinalIgnoreCase)
                               .Replace(prefix.Replace('\\', '/'), Redacted, StringComparison.OrdinalIgnoreCase);
            }
        result = FileUri.Replace(result, Redacted);
        result = DrivePath.Replace(result, Redacted);
        result = UncPath.Replace(result, Redacted);
        result = HomePath.Replace(result, Redacted);
        result = AbsolutePosixPath.Replace(result, Redacted);
        return result;
    }

    /// <summary>Returns the first path-like fragment still present, else null.
    /// Payloads are usually serialized JSON, where one real backslash travels
    /// as two ("C:\\Users"). Matching runs on the unescaped form so JSON
    /// encoding neither hides a real path nor turns a scrubbed single-backslash
    /// remnant ("[local-path]\takes\x") into a false UNC hit.</summary>
    public static string? FindPathLike(string payload)
    {
        var unescaped = payload.Replace("\\\\", "\\");
        foreach (var pattern in new[] { DrivePath, UncPath, HomePath, FileUri, AbsolutePosixPath })
        {
            var match = pattern.Match(unescaped);
            if (match.Success) return match.Value;
        }
        return null;
    }
}
