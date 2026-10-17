using Azure;
using Azure.AI.DocumentIntelligence;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using AnalyzedDocument = DocAssistant.Core.Domain.AnalyzedDocument;

namespace DocAssistant.Infrastructure.Analysis;

/// <summary>
/// Azure AI Document Intelligence: the prebuilt-layout model for text in reading order, and prebuilt-invoice
/// for structured fields when the file looks like an invoice. Not exercised in this repository's tests (it
/// needs an endpoint); the production path only.
/// </summary>
public sealed class DocumentIntelligenceAnalyzer(DocumentIntelligenceClient client) : IDocumentAnalyzer
{
    public string Name => "azure-document-intelligence";

    public async Task<AnalyzedDocument> AnalyzeAsync(Stream content, string fileName, CancellationToken ct)
    {
        var data = await BinaryData.FromStreamAsync(content, ct);
        var layout = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", data, cancellationToken: ct);
        var pages = layout.Value.Pages
            .Select(p => new AnalyzedPage(p.PageNumber, string.Join('\n', p.Lines.Select(l => l.Content))))
            .ToList();

        IReadOnlyDictionary<string, string>? prebuilt = null;
        if (fileName.Contains("invoice", StringComparison.OrdinalIgnoreCase) || layout.Value.Content.Contains("invoice", StringComparison.OrdinalIgnoreCase))
        {
            content.Position = 0;
            var invoice = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", data, cancellationToken: ct);
            var doc = invoice.Value.Documents.FirstOrDefault();
            if (doc is not null)
            {
                prebuilt = doc.Fields.ToDictionary(f => f.Key, f => f.Value.Content ?? string.Empty);
            }
        }
        return new AnalyzedDocument(pages, Name, prebuilt);
    }
}
