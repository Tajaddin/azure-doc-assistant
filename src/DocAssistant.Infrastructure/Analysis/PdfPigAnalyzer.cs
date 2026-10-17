using System.Text;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DocAssistant.Infrastructure.Analysis;

/// <summary>
/// Local, free analyzer: PdfPig words grouped into blocks by the docstrum segmenter, read in reading order.
/// Good for born-digital PDFs (which synthetic and most business documents are); it does not OCR scans.
/// </summary>
public sealed class PdfPigAnalyzer : IDocumentAnalyzer
{
    public string Name => "pdfpig";

    public Task<AnalyzedDocument> AnalyzeAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(content);
        var pages = new List<AnalyzedPage>(pdf.NumberOfPages);
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var sb = new StringBuilder();
            foreach (var block in blocks.OrderByDescending(b => b.BoundingBox.Top).ThenBy(b => b.BoundingBox.Left))
            {
                foreach (var line in block.TextLines)
                {
                    sb.AppendLine(string.Join(' ', line.Words.Select(w => w.Text)));
                }
                sb.AppendLine();
            }
            var text = sb.Length > 0 ? sb.ToString() : page.Text;
            pages.Add(new AnalyzedPage(page.Number, text.Trim()));
        }
        return Task.FromResult(new AnalyzedDocument(pages, Name));
    }
}
