using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DocAssistant.UnitTests;

/// <summary>In-memory ports so the orchestration, the pipeline and the worker loop run without containers.</summary>
public sealed class InMemoryIndex : IDocumentIndex
{
    public readonly List<(Chunk Chunk, float[] Vector)> Items = [];
    public readonly Dictionary<Guid, string> FileNames = [];

    public Task UpsertAsync(IReadOnlyList<(Chunk Chunk, float[] Vector)> items, CancellationToken ct)
    {
        Items.AddRange(items);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest r, CancellationToken ct)
    {
        IReadOnlyList<SearchHit> hits = Items
            .Where(i => i.Chunk.OwnerId == r.OwnerId && (r.DocumentId is null || i.Chunk.DocumentId == r.DocumentId))
            .Select(i => new SearchHit(i.Chunk, FileNames.GetValueOrDefault(i.Chunk.DocumentId, "doc.pdf"), i.Vector.Zip(r.QueryVector).Sum(p => p.First * p.Second), null, null))
            .OrderByDescending(h => h.Score).Take(r.Top).ToList();
        return Task.FromResult(hits);
    }

    public Task DeleteDocumentAsync(Guid documentId, CancellationToken ct)
    {
        Items.RemoveAll(i => i.Chunk.DocumentId == documentId);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLedger : ICostLedger
{
    public readonly List<CostEntry> Entries = [];

    public Task RecordAsync(CostEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<CostSummary> SummaryAsync(string ownerId, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(new CostSummary(Entries.Sum(e => e.CostUsd), Entries.Count, 0, 0, [], []));
}

public sealed class InMemoryRepo : IDocumentRepository
{
    public readonly ConcurrentDictionary<Guid, DocumentRecord> Docs = new();
    public readonly ConcurrentDictionary<Guid, IReadOnlyList<AnalyzedPage>> Pages = new();
    public readonly ConcurrentDictionary<Guid, (InvoiceFields Fields, string Method)> Fields = new();

    public Task CreateAsync(DocumentRecord document, CancellationToken ct)
    {
        Docs[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task<DocumentRecord?> GetAsync(string ownerId, Guid id, CancellationToken ct) =>
        Task.FromResult(Docs.TryGetValue(id, out var d) && d.OwnerId == ownerId ? d : null);

    public Task<IReadOnlyList<DocumentRecord>> ListAsync(string ownerId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentRecord>>(Docs.Values.Where(d => d.OwnerId == ownerId).ToList());

    public Task SetStatusAsync(Guid id, DocumentStatus status, string? error, CancellationToken ct)
    {
        Docs[id] = Docs[id] with { Status = status, Error = error };
        return Task.CompletedTask;
    }

    public Task SetIndexedAsync(Guid id, DocumentKind kind, int pageCount, int chunkCount, CancellationToken ct)
    {
        Docs[id] = Docs[id] with { Status = DocumentStatus.Ready, Kind = kind, PageCount = pageCount, ChunkCount = chunkCount, IndexedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    public Task SavePagesAsync(Guid id, IReadOnlyList<AnalyzedPage> pages, CancellationToken ct)
    {
        Pages[id] = pages;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AnalyzedPage>> GetPagesAsync(string ownerId, Guid id, CancellationToken ct) =>
        Task.FromResult(Docs.TryGetValue(id, out var d) && d.OwnerId == ownerId && Pages.TryGetValue(id, out var p) ? p : []);

    public Task SaveInvoiceFieldsAsync(Guid id, InvoiceFields fields, string method, CancellationToken ct)
    {
        Fields[id] = (fields, method);
        return Task.CompletedTask;
    }

    public Task<(InvoiceFields Fields, string Method)?> GetInvoiceFieldsAsync(string ownerId, Guid id, CancellationToken ct) =>
        Task.FromResult(Fields.TryGetValue(id, out var f) ? f : ((InvoiceFields, string)?)null);

    public Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken ct) => Task.FromResult(Docs.TryRemove(id, out _));
}

public sealed class InMemoryStore : IDocumentStore
{
    public readonly Dictionary<string, byte[]> Blobs = [];

    public async Task SaveAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Blobs[blobName] = ms.ToArray();
    }

    public Task<Stream> OpenAsync(string blobName, CancellationToken ct) =>
        Blobs.TryGetValue(blobName, out var b) ? Task.FromResult<Stream>(new MemoryStream(b)) : throw new FileNotFoundException(blobName);

    public Task DeleteAsync(string blobName, CancellationToken ct)
    {
        Blobs.Remove(blobName);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryQueue : IIngestionQueue
{
    private readonly ConcurrentQueue<IngestionMessage> _queue = new();
    public int Completed;

    public Task EnqueueAsync(IngestionMessage message, CancellationToken ct)
    {
        _queue.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<QueuedIngestion?> DequeueAsync(CancellationToken ct) =>
        Task.FromResult(_queue.TryDequeue(out var m) ? new QueuedIngestion(m, "r", Guid.NewGuid().ToString(), 1) : null);

    public Task CompleteAsync(QueuedIngestion item, CancellationToken ct)
    {
        Interlocked.Increment(ref Completed);
        return Task.CompletedTask;
    }
}

/// <summary>A chat service that replays a script: each round is either text or a set of tool calls.</summary>
public sealed class ScriptedChatService(params ScriptedChatService.Round[] rounds) : IChatCompletionService
{
    public sealed record Round(string? Text, params (string Function, string ArgumentsJson)[] Calls);

    private int _next;
    public int Calls => _next;
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    private Round Take() => rounds[Math.Min(_next++, rounds.Length - 1)];

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        var round = Take();
        var message = new ChatMessageContent(AuthorRole.Assistant, round.Text ?? string.Empty);
        foreach (var (fn, args) in round.Calls)
        {
            message.Items.Add(new FunctionCallContent(fn.Split('-')[1], fn.Split('-')[0], Guid.NewGuid().ToString("N"), new KernelArguments(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(args)!)));
        }
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>([message]);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var round = Take();
        var i = 0;
        foreach (var (fn, args) in round.Calls)
        {
            var update = new StreamingChatMessageContent(AuthorRole.Assistant, null);
            update.Items.Add(new StreamingFunctionCallUpdateContent(Guid.NewGuid().ToString("N"), fn, args, i++));
            yield return update;
        }
        if (round.Text is not null)
        {
            foreach (var word in round.Text.Split(' '))
            {
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, word + " ");
            }
        }
        var usage = new Dictionary<string, object?> { ["Usage"] = new Dictionary<string, object?> { ["InputTokens"] = 100, ["OutputTokens"] = 20 } };
        yield return new StreamingChatMessageContent(AuthorRole.Assistant, string.Empty, metadata: usage);
        await Task.Yield();
    }
}
