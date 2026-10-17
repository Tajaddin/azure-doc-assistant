using System.Diagnostics;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace DocAssistant.Core.Ingestion;

/// <summary>
/// analyze -> chunk -> embed -> index -> mark ready. One class, hosted by the Azure Function (blob trigger)
/// in production and by the worker service locally, so both paths run identical code.
/// </summary>
public sealed class IngestionPipeline(
    IDocumentStore store,
    IDocumentAnalyzer analyzer,
    IEmbedder embedder,
    IDocumentIndex index,
    IDocumentRepository documents,
    ILogger<IngestionPipeline> log,
    TextChunker? chunker = null)
{
    private readonly TextChunker _chunker = chunker ?? new TextChunker();

    public async Task<IngestionResult> RunAsync(IngestionMessage message, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        await documents.SetStatusAsync(message.DocumentId, DocumentStatus.Processing, null, ct);
        try
        {
            var analyzeTimer = Stopwatch.StartNew();
            AnalyzedDocument analyzed;
            await using (var content = await store.OpenAsync(message.BlobName, ct))
            {
                analyzed = await analyzer.AnalyzeAsync(content, message.FileName, ct);
            }
            analyzeTimer.Stop();
            await documents.SavePagesAsync(message.DocumentId, analyzed.Pages, ct);

            var chunks = _chunker.Chunk(message.DocumentId, message.OwnerId, analyzed.Pages);
            if (chunks.Count == 0)
            {
                throw new InvalidOperationException("no text could be extracted from the document");
            }

            var embedTimer = Stopwatch.StartNew();
            var vectors = new List<float[]>(chunks.Count);
            foreach (var batch in chunks.Chunk(32))
            {
                vectors.AddRange(await embedder.EmbedAsync(batch.Select(c => c.Text).ToList(), ct));
            }
            embedTimer.Stop();

            var indexTimer = Stopwatch.StartNew();
            await index.DeleteDocumentAsync(message.DocumentId, ct); // idempotent re-ingestion
            await index.UpsertAsync(chunks.Zip(vectors).Select(p => (p.First, p.Second)).ToList(), ct);
            indexTimer.Stop();

            var kind = DocumentClassifier.Classify(message.FileName, analyzed.Pages);
            await documents.SetIndexedAsync(message.DocumentId, kind, analyzed.PageCount, chunks.Count, ct);
            total.Stop();

            var result = new IngestionResult(
                message.DocumentId, analyzed.PageCount, chunks.Count,
                analyzeTimer.Elapsed, embedTimer.Elapsed, indexTimer.Elapsed, total.Elapsed);
            log.LogInformation(
                "ingested {DocumentId} {FileName}: {Pages} pages, {Chunks} chunks, {Kind}, analyze {AnalyzeMs} ms, embed {EmbedMs} ms, index {IndexMs} ms, total {TotalMs} ms, {PagesPerMinute:F1} pages/min",
                message.DocumentId, message.FileName, result.Pages, result.Chunks, kind,
                (int)result.Analyze.TotalMilliseconds, (int)result.Embed.TotalMilliseconds,
                (int)result.Index.TotalMilliseconds, (int)result.Total.TotalMilliseconds, result.PagesPerMinute);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "ingestion failed for {DocumentId} {FileName}", message.DocumentId, message.FileName);
            await documents.SetStatusAsync(message.DocumentId, DocumentStatus.Failed, ex.Message, CancellationToken.None);
            throw;
        }
    }
}

public static class DocumentClassifier
{
    /// <summary>A cheap heuristic on the first page; the model is not consulted for classification.</summary>
    public static DocumentKind Classify(string fileName, IReadOnlyList<AnalyzedPage> pages)
    {
        var head = (pages.Count > 0 ? pages[0].Text : string.Empty).ToLowerInvariant();
        var name = fileName.ToLowerInvariant();
        if (name.Contains("invoice") || name.StartsWith("inv") || head.Contains("invoice number") || head.Contains("invoice #") || head.Contains("amount due"))
        {
            return DocumentKind.Invoice;
        }
        if (name.Contains("contract") || name.Contains("agreement") || name.StartsWith("msa") || head.Contains("agreement") || head.Contains("hereinafter"))
        {
            return DocumentKind.Contract;
        }
        if (name.Contains("manual") || head.Contains("user manual") || head.Contains("troubleshooting") || head.Contains("specifications"))
        {
            return DocumentKind.Manual;
        }
        return DocumentKind.Unknown;
    }
}
