using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DocAssistant.Functions;

/// <summary>
/// Blob-triggered ingestion: the API writes {ownerId}/{documentId}.pdf, the trigger fires, and the same
/// pipeline the worker uses runs here. The blob path carries both ids, so no queue round trip is needed.
/// </summary>
public sealed class IngestBlobFunction(IngestionPipeline pipeline, IDocumentRepository documents, ILogger<IngestBlobFunction> log)
{
    [Function("IngestBlob")]
    public async Task RunAsync(
        [BlobTrigger("documents/{ownerId}/{documentId}.pdf", Connection = "Storage__ConnectionString")] Stream _,
        string ownerId,
        string documentId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(documentId, out var id))
        {
            log.LogWarning("ignoring blob with a non-guid name {Name}", documentId);
            return;
        }
        var doc = await documents.GetAsync(ownerId, id, ct);
        if (doc is null)
        {
            log.LogWarning("no document record for {DocumentId}; the API must create it before the upload", id);
            return;
        }
        if (doc.Status == DocumentStatus.Ready)
        {
            log.LogInformation("{DocumentId} already indexed; skipping", id);
            return;
        }
        var result = await pipeline.RunAsync(new IngestionMessage(id, ownerId, doc.BlobName, doc.FileName), ct);
        log.LogInformation("function ingested {DocumentId}: {Pages} pages in {Ms} ms", id, result.Pages, (int)result.Total.TotalMilliseconds);
    }
}
