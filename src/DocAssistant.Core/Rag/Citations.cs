using System.Text.RegularExpressions;
using DocAssistant.Core.Domain;

namespace DocAssistant.Core.Rag;

public static partial class Citations
{
    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex MarkerPattern();

    /// <summary>Numbered source block the model sees: [1] file.pdf, page 2: text ...</summary>
    public static string FormatSources(IReadOnlyList<SearchHit> hits, int maxCharsPerSource = 1500)
    {
        if (hits.Count == 0)
        {
            return "(no sources found)";
        }
        return string.Join("\n\n", hits.Select((h, i) =>
        {
            var text = h.Chunk.Text.Length > maxCharsPerSource ? h.Chunk.Text[..maxCharsPerSource] + " ..." : h.Chunk.Text;
            return $"[{i + 1}] {h.FileName}, page {h.Chunk.Page}:\n{text}";
        }));
    }

    /// <summary>Maps [n] markers in the answer to the sources they refer to, in order of first appearance.</summary>
    public static IReadOnlyList<Citation> Parse(string answer, IReadOnlyList<SearchHit> hits)
    {
        var seen = new HashSet<int>();
        var result = new List<Citation>();
        foreach (Match m in MarkerPattern().Matches(answer))
        {
            var n = int.Parse(m.Groups[1].Value);
            if (n < 1 || n > hits.Count || !seen.Add(n))
            {
                continue;
            }
            var hit = hits[n - 1];
            result.Add(new Citation(n, hit.Chunk.DocumentId, hit.FileName, hit.Chunk.Page, hit.Chunk.Id, Snippet(hit.Chunk.Text)));
        }
        return result;
    }

    public static string Snippet(string text, int maxChars = 240)
    {
        var oneLine = Regex.Replace(text, @"\s+", " ").Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine[..maxChars].TrimEnd() + "...";
    }
}
