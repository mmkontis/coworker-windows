using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Coworker.Services.Updates;

public enum NoteBlockKind
{
    Heading,
    Paragraph,
    ListItem,
}

public sealed record NoteRun(string Text, bool Bold = false, bool Italic = false, string? Href = null);

/// <param name="Level">Heading level 1-6, or the list nesting depth (0 = top level) for list items.</param>
/// <param name="Marker">"•" or "1." for list items.</param>
public sealed record NoteBlock(NoteBlockKind Kind, int Level, string? Marker, IReadOnlyList<NoteRun> Runs);

/// <summary>
/// Turns the appcast's HTML release notes (Sparkle shows them in a web view) into a few native blocks:
/// headings, paragraphs, bullet/numbered lists, bold, italic and links. Anything else is dropped to
/// its text. Tolerant of sloppy HTML (unclosed li, bare br) and never executes or loads anything.
/// </summary>
public static class ReleaseNotesParser
{
    private static readonly Regex Token = new(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)\b([^>]*)>|([^<]+)|<", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Href = new(@"href\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<NoteBlock> Parse(string? html)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(html)) return blocks;

        var runs = new List<NoteRun>();
        var lists = new Stack<(bool Ordered, int Count)>();
        NoteBlockKind kind = NoteBlockKind.Paragraph;
        var level = 0;
        string? marker = null;
        var bold = 0;
        var italic = 0;
        string? href = null;
        var skipDepth = 0;   // inside <script>/<style>

        void Flush()
        {
            // Collapse whitespace and trim the edges of the block.
            var merged = new List<NoteRun>();
            foreach (var run in runs)
            {
                var text = Regex.Replace(run.Text, @"[ \t\r\n ]+", " ");
                if (text.Length == 0) continue;
                if (merged.Count > 0 && merged[^1] is { } last && last.Bold == run.Bold && last.Italic == run.Italic && last.Href == run.Href)
                    merged[^1] = last with { Text = last.Text + text };
                else
                    merged.Add(run with { Text = text });
            }
            if (merged.Count > 0) merged[0] = merged[0] with { Text = merged[0].Text.TrimStart() };
            if (merged.Count > 0) merged[^1] = merged[^1] with { Text = merged[^1].Text.TrimEnd() };
            merged.RemoveAll(r => r.Text.Length == 0);
            if (merged.Count > 0) blocks.Add(new NoteBlock(kind, level, marker, merged));
            runs = new List<NoteRun>();
            kind = NoteBlockKind.Paragraph;
            level = 0;
            marker = null;
        }

        foreach (Match match in Token.Matches(html))
        {
            if (match.Groups[4].Success)
            {
                if (skipDepth == 0) runs.Add(new NoteRun(WebUtility.HtmlDecode(match.Groups[4].Value), bold > 0, italic > 0, href));
                continue;
            }
            if (!match.Groups[2].Success)
            {
                // A comment is dropped; a "<" that does not start a tag is ordinary text ("a < b").
                if (match.Value == "<" && skipDepth == 0) runs.Add(new NoteRun("<", bold > 0, italic > 0, href));
                continue;
            }

            var closing = match.Groups[1].Value == "/";
            var tag = match.Groups[2].Value.ToLowerInvariant();
            var attributes = match.Groups[3].Value;

            if (tag is "script" or "style")
            {
                skipDepth = Math.Max(0, skipDepth + (closing ? -1 : 1));
                continue;
            }
            if (skipDepth > 0) continue;

            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush();
                    if (!closing)
                    {
                        kind = NoteBlockKind.Heading;
                        level = tag[1] - '0';
                    }
                    break;

                case "p" or "div" or "tr" or "table" or "blockquote":
                    Flush();
                    break;

                case "ul" or "ol":
                    Flush();
                    if (!closing) lists.Push((tag == "ol", 0));
                    else if (lists.Count > 0) lists.Pop();
                    break;

                case "li":
                    Flush();
                    if (!closing)
                    {
                        kind = NoteBlockKind.ListItem;
                        level = Math.Max(0, lists.Count - 1);
                        if (lists.Count > 0 && lists.Peek().Ordered)
                        {
                            var (ordered, count) = lists.Pop();
                            lists.Push((ordered, count + 1));
                            marker = $"{count + 1}.";
                        }
                        else
                        {
                            marker = "•";
                        }
                    }
                    break;

                case "br":
                    runs.Add(new NoteRun(" ", bold > 0, italic > 0, href));
                    break;

                case "b" or "strong":
                    bold = Math.Max(0, bold + (closing ? -1 : 1));
                    break;

                case "i" or "em":
                    italic = Math.Max(0, italic + (closing ? -1 : 1));
                    break;

                case "a":
                    if (closing)
                    {
                        href = null;
                    }
                    else
                    {
                        var m = Href.Match(attributes);
                        var target = m.Success ? WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
                        // Only web links are ever offered; javascript:, file: etc. degrade to plain text.
                        href = target != null && Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? target : null;
                    }
                    break;
            }
        }
        Flush();
        return blocks;
    }

    /// Plain text of the blocks, one line each (used by tests and as an accessibility fallback).
    public static string ToPlainText(IReadOnlyList<NoteBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (sb.Length > 0) sb.Append('\n');
            if (block.Marker != null) sb.Append(block.Marker).Append(' ');
            foreach (var run in block.Runs) sb.Append(run.Text);
        }
        return sb.ToString();
    }
}
