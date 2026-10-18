using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuickClip.Updates;

/// <summary>One release's notes, as written on GitHub (Markdown).</summary>
public sealed record ReleaseNote(AppVersion Version, string Title, string Body, string Url);

public enum NoteBlockKind { Heading, Bullet, Paragraph }
public enum NoteSpanKind { Text, Bold, Code, Link }

/// <summary>A piece of a line: plain, bold, code or a link.</summary>
public sealed record NoteSpan(NoteSpanKind Kind, string Text, string? Url = null);

/// <summary>A heading (Level 1-6), a bullet (Level = how far it's indented, from 0) or a paragraph.</summary>
public sealed record NoteBlock(NoteBlockKind Kind, int Level, IReadOnlyList<NoteSpan> Spans);

/// <summary>"What's new" after an update: which releases to show, fetched from GitHub, and their Markdown read.</summary>
public static partial class ReleaseNotes
{
    /// <summary>
    /// Whether to show what's new when the app starts, and since which version (exclusive; null for just this one).
    /// <paramref name="seenVersion"/>: the version whose notes were last seen, or null if none ever were;
    /// <paramref name="hadSettings"/>: QuickClip had run before (a fresh install has nothing new to show).
    /// </summary>
    public static (bool Show, AppVersion? After) Check(string? seenVersion, bool hadSettings, AppVersion current)
    {
        if (seenVersion == null) return (hadSettings, null); // updated from a version from before this existed
        if (!AppVersion.TryParse(seenVersion, out var seen)) return (false, null);
        return current > seen ? (true, seen) : (false, null);
    }

    /// <summary>
    /// The releases after <paramref name="after"/> up to and including <paramref name="current"/>, newest first
    /// (just <paramref name="current"/>'s when <paramref name="after"/> is null). Nightly builds only if this is one.
    /// </summary>
    public static List<ReleaseNote> Select(string json, AppVersion? after, AppVersion current)
    {
        var notes = new List<ReleaseNote>();
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!AppVersion.TryParse(tag, out var version)) continue;
            bool prerelease = version.IsPrerelease || (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
            if (prerelease && !current.IsPrerelease) continue;
            bool wanted = after == null ? version.CompareTo(current) == 0 : version > after && !(version > current);
            if (!wanted) continue;
            notes.Add(new ReleaseNote(version,
                release.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : $"QuickClip {version.Display}",
                release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                release.TryGetProperty("html_url", out var u) ? u.GetString() ?? Updater.ReleasesPage : Updater.ReleasesPage));
        }
        notes.Sort((a, b) => b.Version.CompareTo(a.Version));
        return notes;
    }

    public static async Task<List<ReleaseNote>> FetchAsync(AppVersion? after, AppVersion current, CancellationToken ct) =>
        Select(await Updater.GetReleasesJsonAsync(ct), after, current);

    /// <summary>The Markdown GitHub release notes use: headings, bullets (nested by indenting), paragraphs, bold, code and links.</summary>
    public static List<NoteBlock> Parse(string markdown)
    {
        var blocks = new List<NoteBlock>();
        NoteBlockKind? kind = null;
        int level = 0;
        var text = new System.Text.StringBuilder();

        void Flush()
        {
            if (kind is { } k && text.Length > 0) blocks.Add(new NoteBlock(k, level, Spans(text.ToString().Trim())));
            kind = null;
            text.Clear();
        }

        foreach (string raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            string trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (HeadingPattern().Match(trimmed) is { Success: true } heading)
            {
                Flush();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, heading.Groups[1].Length, Spans(heading.Groups[2].Value.Trim())));
                continue;
            }
            if (BulletPattern().Match(trimmed) is { Success: true } bullet)
            {
                Flush();
                kind = NoteBlockKind.Bullet;
                level = indent / 2;
                text.Append(bullet.Groups[1].Value);
                continue;
            }
            // A line of a paragraph, or more of the bullet above (text runs on across lines).
            if (kind == null)
            {
                kind = NoteBlockKind.Paragraph;
                level = 0;
            }
            if (text.Length > 0) text.Append(' ');
            text.Append(trimmed);
        }
        Flush();
        return blocks;
    }

    /// <summary>Splits a line into plain text, **bold**, `code` and [links](https://…).</summary>
    public static List<NoteSpan> Spans(string line)
    {
        var spans = new List<NoteSpan>();
        int at = 0;
        foreach (Match m in InlinePattern().Matches(line))
        {
            if (m.Index > at) spans.Add(new NoteSpan(NoteSpanKind.Text, line[at..m.Index]));
            if (m.Groups["bold"].Success) spans.Add(new NoteSpan(NoteSpanKind.Bold, m.Groups["bold"].Value));
            else if (m.Groups["code"].Success) spans.Add(new NoteSpan(NoteSpanKind.Code, m.Groups["code"].Value));
            else spans.Add(new NoteSpan(NoteSpanKind.Link, m.Groups["text"].Value, m.Groups["url"].Value));
            at = m.Index + m.Length;
        }
        if (at < line.Length) spans.Add(new NoteSpan(NoteSpanKind.Text, line[at..]));
        return spans;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingPattern();
    [GeneratedRegex(@"^[-*+]\s+(.*)$")] private static partial Regex BulletPattern();
    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|\[(?<text>[^\]]+)\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex InlinePattern();
}
