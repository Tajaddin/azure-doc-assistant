using System.Text;
using DocAssistant.Core.Domain;

namespace DocAssistant.Core.Ingestion;

/// <summary>
/// Splits analyzed pages into retrieval chunks. Chunks never cross a page (so a citation is one page), prefer
/// paragraph boundaries, stay under a character budget (roughly 4 characters per token), and overlap by a
/// tail of the previous chunk so a fact on a boundary is retrievable from either side.
/// </summary>
public sealed class TextChunker
{
    public int MaxChars { get; init; } = 1800;
    public int OverlapChars { get; init; } = 200;
    public int MinChars { get; init; } = 40;

    public IReadOnlyList<Chunk> Chunk(Guid documentId, string ownerId, IReadOnlyList<AnalyzedPage> pages)
    {
        var chunks = new List<Chunk>();
        var ordinal = 0;
        foreach (var page in pages)
        {
            foreach (var text in SplitPage(page.Text))
            {
                chunks.Add(new Chunk(Guid.NewGuid(), documentId, ownerId, page.Number, ordinal++, text));
            }
        }
        return chunks;
    }

    public IReadOnlyList<string> SplitPage(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length < MinChars)
        {
            return normalized.Length == 0 ? [] : [normalized];
        }
        if (normalized.Length <= MaxChars)
        {
            return [normalized];
        }

        var paragraphs = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs.SelectMany(SplitLongParagraph))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > MaxChars)
            {
                pieces.Add(current.ToString());
                var overlap = Tail(current.ToString(), OverlapChars);
                current.Clear();
                if (overlap.Length > 0)
                {
                    current.Append(overlap).Append("\n\n");
                }
            }
            if (current.Length > 0)
            {
                current.Append("\n\n");
            }
            current.Append(paragraph);
        }
        if (current.Length >= MinChars)
        {
            pieces.Add(current.ToString());
        }
        else if (pieces.Count > 0 && current.Length > 0)
        {
            pieces[^1] = pieces[^1] + "\n\n" + current;
        }
        return pieces;
    }

    private IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        if (paragraph.Length <= MaxChars)
        {
            yield return paragraph;
            yield break;
        }
        // sentence-ish split, then hard split as a last resort
        var start = 0;
        while (start < paragraph.Length)
        {
            var end = Math.Min(start + MaxChars, paragraph.Length);
            if (end < paragraph.Length)
            {
                var cut = paragraph.LastIndexOfAny(['.', '!', '?', ';', '\n'], end - 1, end - start);
                if (cut > start + MaxChars / 2)
                {
                    end = cut + 1;
                }
            }
            yield return paragraph[start..end].Trim();
            start = end;
        }
    }

    private static string Tail(string text, int chars)
    {
        if (chars <= 0 || text.Length <= chars)
        {
            return text.Length <= chars ? text : string.Empty;
        }
        var tail = text[^chars..];
        var firstBreak = tail.IndexOfAny([' ', '\n']);
        return firstBreak >= 0 ? tail[(firstBreak + 1)..].Trim() : tail.Trim();
    }

    public static string Normalize(string text)
    {
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(s.Length);
        var newlines = 0;
        foreach (var ch in s)
        {
            if (ch == '\n')
            {
                newlines++;
                if (newlines <= 2)
                {
                    sb.Append('\n');
                }
                continue;
            }
            newlines = 0;
            if (ch == '\t' || ch == ' ')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsControl(ch))
            {
                continue;
            }
            sb.Append(ch);
        }
        var lines = sb.ToString().Split('\n').Select(l => l.Trim());
        return string.Join('\n', lines).Trim();
    }
}
