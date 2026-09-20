using System.Text;

namespace GameDirector.Client;

/// <summary>Caption text is literal viewer-facing copy, never markup. The SRT
/// writer owns timestamp formatting and every escape rule in one place, so the
/// job producer cannot invent its own (and get libass injection half-right).</summary>
public static class SubtitleRenderer
{
    public static string FormatTimestamp(double seconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    /// <summary>Neutralize anything a subtitle filter could read as styling:
    /// HTML-ish tags (&amp; &lt; &gt; only — quotes and apostrophes are plain
    /// text in SRT), ASS override blocks, control characters. Blank lines are
    /// collapsed so one caption cannot terminate the SRT entry early.</summary>
    public static string Escape(string text)
    {
        var normalized = text.Replace("\r", "").Replace("\0", "");
        while (normalized.Contains("\n\n")) normalized = normalized.Replace("\n\n", "\n");
        var escaped = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '{': escaped.Append('('); break;
                case '}': escaped.Append(')'); break;
                default: if (!char.IsControl(c) || c == '\n') escaped.Append(c); break;
            }
        }
        return escaped.ToString();
    }

    /// <summary>Render ordered, non-overlapping captions (already validated by
    /// FilmCompiler.ValidateSound) as a complete SRT document.</summary>
    public static string ToSrt(IReadOnlyList<FilmSubtitle> entries) =>
        string.Join("\n", entries.Select((s, i) =>
            $"{i + 1}\n{FormatTimestamp(s.Start)} --> {FormatTimestamp(s.End)}\n{Escape(s.Text)}\n"));
}
