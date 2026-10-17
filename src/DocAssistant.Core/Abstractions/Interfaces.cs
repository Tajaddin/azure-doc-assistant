using DocAssistant.Core.Domain;

namespace DocAssistant.Core.Abstractions;

/// <summary>Turns a PDF into pages of text. Azure Document Intelligence in production, PdfPig locally.</summary>
public interface IDocumentAnalyzer
{
    string Name { get; }
    Task<AnalyzedDocument> AnalyzeAsync(Stream content, string fileName, CancellationToken ct);
}

/// <summary>Text to vectors. Azure OpenAI embeddings in production, Ollama locally, a hash embedder in tests.</summary>
public interface IEmbedder
{
    string Model { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

/// <summary>Hybrid (vector + keyword) index scoped by owner. Azure AI Search in production, pgvector locally.</summary>
public interface IDocumentIndex
{
    Task UpsertAsync(IReadOnlyList<(Chunk Chunk, float[] Vector)> items, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken ct);
}

public sealed record SearchRequest(string OwnerId, string Query, float[] QueryVector, int Top = 8, Guid? DocumentId = null);

/// <summary>Where the original files live. Azure Blob Storage; Azurite locally with the same SDK.</summary>
public interface IDocumentStore
{
    Task SaveAsync(string blobName, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenAsync(string blobName, CancellationToken ct);
    Task DeleteAsync(string blobName, CancellationToken ct);
}

/// <summary>Hands uploads to the ingestion host (a Function's blob trigger in Azure, the worker locally).</summary>
public interface IIngestionQueue
{
    Task EnqueueAsync(IngestionMessage message, CancellationToken ct);
    Task<QueuedIngestion?> DequeueAsync(CancellationToken ct);
    Task CompleteAsync(QueuedIngestion item, CancellationToken ct);
}

public sealed record QueuedIngestion(IngestionMessage Message, string Receipt, string MessageId, int DequeueCount);

public interface IDocumentRepository
{
    Task CreateAsync(DocumentRecord document, CancellationToken ct);
    Task<DocumentRecord?> GetAsync(string ownerId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<DocumentRecord>> ListAsync(string ownerId, CancellationToken ct);
    Task SetStatusAsync(Guid id, DocumentStatus status, string? error, CancellationToken ct);
    Task SetIndexedAsync(Guid id, DocumentKind kind, int pageCount, int chunkCount, CancellationToken ct);
    Task SavePagesAsync(Guid id, IReadOnlyList<AnalyzedPage> pages, CancellationToken ct);
    Task<IReadOnlyList<AnalyzedPage>> GetPagesAsync(string ownerId, Guid id, CancellationToken ct);
    Task SaveInvoiceFieldsAsync(Guid id, InvoiceFields fields, string method, CancellationToken ct);
    Task<(InvoiceFields Fields, string Method)?> GetInvoiceFieldsAsync(string ownerId, Guid id, CancellationToken ct);
    Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken ct);
}

public interface ICostLedger
{
    Task RecordAsync(CostEntry entry, CancellationToken ct);
    Task<CostSummary> SummaryAsync(string ownerId, DateTimeOffset since, CancellationToken ct);
}

/// <summary>Extracts invoice fields. The LLM extractor uses Semantic Kernel; the regex extractor is the no-model baseline.</summary>
public interface IInvoiceExtractor
{
    string Method { get; }
    Task<ExtractionResult> ExtractAsync(IReadOnlyList<AnalyzedPage> pages, string ownerId, CancellationToken ct);
}
