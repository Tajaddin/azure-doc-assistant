using System.Runtime.CompilerServices;
using DocAssistant.Api.Endpoints;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Extraction;
using DocAssistant.Core.Ingestion;
using DocAssistant.Core.Pricing;
using DocAssistant.Core.Rag;
using DocAssistant.Infrastructure.Ai;
using DocAssistant.Infrastructure.Analysis;
using DocAssistant.SynthData;
using DocAssistant.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DocAssistant.UnitTests;

/// <summary>The tool loop, the LLM extractor and the worker loop, driven by scripted models and in-memory ports.</summary>
public class WorkflowTests
{
    private static Kernel KernelWith(IChatCompletionService chat)
    {
        var b = Kernel.CreateBuilder();
        b.Services.AddSingleton(chat);
        b.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashEmbeddingGenerator(256));
        return b.Build();
    }

    private static (InMemoryRepo Repo, InMemoryIndex Index, Guid DocId) Seeded(string owner, SyntheticCorpus.Invoice inv, string pageText)
    {
        var repo = new InMemoryRepo();
        var index = new InMemoryIndex();
        var id = Guid.NewGuid();
        repo.Docs[id] = new DocumentRecord(id, owner, inv.FileName, $"{owner}/{id}.pdf", 1, DocumentKind.Invoice, DocumentStatus.Ready, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        repo.Pages[id] = [new AnalyzedPage(1, pageText)];
        index.FileNames[id] = inv.FileName;
        var chunk = new Chunk(Guid.NewGuid(), id, owner, 1, 0, pageText);
        index.Items.Add((chunk, HashEmbeddingGenerator.Embed(pageText, 256)));
        return (repo, index, id);
    }

    private static readonly SyntheticCorpus.Invoice Inv = SyntheticCorpus.Generate(invoices: 1, contracts: 0, manuals: 0).Invoices[0];
    private static readonly string InvoiceText = $"INVOICE\nInvoice Number: {Inv.Number}\nInvoice Date: {Inv.InvoiceDate}\nDue Date: {Inv.DueDate}\nPayment Terms: {Inv.Terms}\nFrom: {Inv.Vendor}\nBill To: {Inv.Customer}\nSubtotal: ${Inv.Subtotal:0.00}\nSales Tax (7%): ${Inv.Tax:0.00}\nTotal Due: ${Inv.Total:0.00}";

    [Fact]
    public async Task Tool_call_is_executed_and_its_result_feeds_the_final_answer()
    {
        var chat = new ScriptedChatService(
            new ScriptedChatService.Round(null, ("invoices-lookup_invoice", $"{{\"fileName\":\"{Inv.FileName}\"}}")),
            new ScriptedChatService.Round($"The total is ${Inv.Total:0.00} [1]."));
        var kernel = KernelWith(chat);
        var (repo, index, docId) = Seeded("alice", Inv, InvoiceText);
        var ledger = new InMemoryLedger();
        var embedder = new SkEmbedder(kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), "hash", 256);
        var svc = new AskService(kernel, embedder, index, repo, new RegexInvoiceExtractor(), ledger, new ModelPricing(), "scripted", NullLogger<AskService>.Instance);

        var events = new List<AskEvent>();
        await foreach (var ev in svc.StreamAsync("alice", $"What is the total of {Inv.FileName}?", null, default))
        {
            events.Add(ev);
        }
        var answer = Assert.IsType<Answer>(events[^1].Data);
        Assert.Equal(2, chat.Calls);
        Assert.Equal([$"lookup_invoice({Inv.FileName})"], answer.ToolCalls);
        Assert.Contains(events, e => e.Type == "tool" && e.Text == "lookup_invoice");
        Assert.Contains($"{Inv.Total:0.00}", answer.Text, StringComparison.Ordinal);
        Assert.Single(answer.Citations);
        Assert.Equal(docId, answer.Citations[0].DocumentId);
        Assert.Equal(2, answer.Usage.Calls);
        Assert.Equal(200, answer.Usage.InputTokens);
        Assert.False(answer.Usage.Estimated);
        Assert.True(repo.Fields.ContainsKey(docId)); // the plugin extracted and stored the fields
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public async Task Tool_loop_is_bounded()
    {
        var chat = new ScriptedChatService(new ScriptedChatService.Round(null, ("invoices-list_documents", "{}")));
        var kernel = KernelWith(chat);
        var (repo, index, _) = Seeded("bob", Inv, InvoiceText);
        var embedder = new SkEmbedder(kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), "hash", 256);
        var svc = new AskService(kernel, embedder, index, repo, new RegexInvoiceExtractor(), new InMemoryLedger(), new ModelPricing(), "scripted", NullLogger<AskService>.Instance);

        var answer = await svc.AskAsync("bob", "list everything forever", null, default);
        Assert.Equal(AskService.MaxRounds, chat.Calls);
        Assert.Equal(AskService.MaxRounds - 1, answer.ToolCalls.Count);
        Assert.Contains("tool-call limit", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_are_recorded_when_the_client_stops_reading_mid_answer()
    {
        // round 1 calls a tool and reports 100 input / 20 output tokens; the client disconnects during round 2
        var chat = new ScriptedChatService(
            new ScriptedChatService.Round(null, ("invoices-list_documents", "{}")),
            new ScriptedChatService.Round($"The total is ${Inv.Total:0.00} [1], as the invoice states."));
        var kernel = KernelWith(chat);
        var (repo, index, _) = Seeded("erin", Inv, InvoiceText);
        var ledger = new InMemoryLedger();
        var embedder = new SkEmbedder(kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), "hash", 256);
        var svc = new AskService(kernel, embedder, index, repo, new RegexInvoiceExtractor(), ledger, new ModelPricing(), "scripted", NullLogger<AskService>.Instance);

        await foreach (var ev in svc.StreamAsync("erin", "What is the total?", null, default))
        {
            if (ev.Type == "token")
            {
                break; // what the SSE endpoint does when a write to a closed connection throws
            }
        }

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(("erin", "ask"), (entry.OwnerId, entry.Operation));
        Assert.True(entry.InputTokens > 100, $"round 1's 100 reported tokens plus round 2's estimated prompt, got {entry.InputTokens}");
        Assert.True(entry.OutputTokens >= 20, $"got {entry.OutputTokens}");
    }

    /// <summary>Takes the request, then sends nothing until the caller gives up.</summary>
    private sealed class SilentChatService : IChatCompletionService
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Called.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }

    [Fact]
    public async Task The_prompt_is_recorded_when_the_request_is_cancelled_before_the_first_token()
    {
        var chat = new SilentChatService();
        var kernel = KernelWith(chat);
        var (repo, index, _) = Seeded("frank", Inv, InvoiceText);
        var ledger = new InMemoryLedger();
        var embedder = new SkEmbedder(kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), "hash", 256);
        var svc = new AskService(kernel, embedder, index, repo, new RegexInvoiceExtractor(), ledger, new ModelPricing(), "scripted", NullLogger<AskService>.Instance);
        using var cts = new CancellationTokenSource();

        var ask = svc.AskAsync("frank", "What is the total?", null, cts.Token);
        await chat.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync(); // the prompt has been sent; the provider bills it

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(("frank", "ask"), (entry.OwnerId, entry.Operation));
        Assert.True(entry.InputTokens > 0);
        Assert.Equal(0, entry.OutputTokens);
    }

    [Fact]
    public async Task Unknown_tool_failure_is_reported_back_to_the_model_not_thrown()
    {
        var chat = new ScriptedChatService(
            new ScriptedChatService.Round(null, ("invoices-lookup_invoice", "{\"fileName\":\"does-not-exist.pdf\"}")),
            new ScriptedChatService.Round("No such invoice."));
        var kernel = KernelWith(chat);
        var (repo, index, _) = Seeded("carol", Inv, InvoiceText);
        var embedder = new SkEmbedder(kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), "hash", 256);
        var svc = new AskService(kernel, embedder, index, repo, new RegexInvoiceExtractor(), new InMemoryLedger(), new ModelPricing(), "scripted", NullLogger<AskService>.Instance);
        var answer = await svc.AskAsync("carol", "total of does-not-exist.pdf?", null, default);
        Assert.Equal("No such invoice.", answer.Text);
        Assert.Empty(answer.Citations);
    }

    [Fact]
    public async Task Llm_extractor_parses_model_json_and_falls_back_to_regex_on_garbage()
    {
        var good = new LlmInvoiceExtractor(KernelFactory.Create(new AiOptions { Provider = "fake" }), new ModelPricing(), "fake", new InMemoryLedger(), NullLogger<LlmInvoiceExtractor>.Instance);
        var result = await good.ExtractAsync([new AnalyzedPage(1, InvoiceText)], "alice", default);
        Assert.Equal("llm", result.Method);
        Assert.Equal(Inv.Number, result.Fields.InvoiceNumber);
        Assert.Equal(Inv.Total, result.Fields.Total);
        Assert.Equal(1, result.Usage.Calls);

        var bad = new LlmInvoiceExtractor(KernelWith(new ScriptedChatService(new ScriptedChatService.Round("I would rather not."))), new ModelPricing(), "scripted", new InMemoryLedger(), NullLogger<LlmInvoiceExtractor>.Instance);
        var fallback = await bad.ExtractAsync([new AnalyzedPage(1, InvoiceText)], "alice", default);
        Assert.Equal("regex-fallback", fallback.Method);
        Assert.Equal(Inv.Number, fallback.Fields.InvoiceNumber);
    }

    [Fact]
    public async Task Worker_loop_processes_a_queued_upload_to_ready()
    {
        var owner = "dave";
        var repo = new InMemoryRepo();
        var store = new InMemoryStore();
        var queue = new InMemoryQueue();
        var index = new InMemoryIndex();
        var id = Guid.NewGuid();
        var blob = $"{owner}/{id}.pdf";
        await store.SaveAsync(blob, new MemoryStream(SyntheticCorpus.RenderInvoice(Inv)), "application/pdf", default);
        repo.Docs[id] = new DocumentRecord(id, owner, Inv.FileName, blob, 1, DocumentKind.Unknown, DocumentStatus.Uploaded, 0, 0, DateTimeOffset.UtcNow, null, null);
        await queue.EnqueueAsync(new IngestionMessage(id, owner, blob, Inv.FileName), default);

        var kernel = KernelFactory.Create(new AiOptions { Provider = "fake" });
        var pipeline = new IngestionPipeline(store, new PdfPigAnalyzer(), KernelFactory.CreateEmbedder(kernel, new AiOptions { Provider = "fake" }), index, repo, NullLogger<IngestionPipeline>.Instance);
        var worker = new IngestionWorker(queue, pipeline, repo, NullLogger<IngestionWorker>.Instance) { IdleDelay = TimeSpan.FromMilliseconds(20) };

        await worker.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (repo.Docs[id].Status != DocumentStatus.Ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        await worker.StopAsync(default);

        var doc = repo.Docs[id];
        Assert.Equal(DocumentStatus.Ready, doc.Status);
        Assert.Equal(DocumentKind.Invoice, doc.Kind);
        Assert.Equal(1, doc.PageCount);
        Assert.True(doc.ChunkCount >= 1);
        Assert.Equal(1, queue.Completed);
        Assert.NotEmpty(index.Items);
        Assert.Contains(Inv.Number, repo.Pages[id][0].Text, StringComparison.Ordinal);
    }
}

/// <summary>The blob trigger fires as soon as the blob exists and looks the record up, so the record must come first.</summary>
public class UploadOrderTests
{
    private sealed class RecordingStore(IDocumentRepository repo, bool fail = false) : IDocumentStore
    {
        public bool RecordExistedAtSave { get; private set; }
        public string? Saved { get; private set; }
        public List<string> Deleted { get; } = [];

        public async Task SaveAsync(string blobName, Stream content, string contentType, CancellationToken ct)
        {
            Saved = blobName;
            RecordExistedAtSave = (await repo.ListAsync("gina", ct)).Any(d => d.BlobName == blobName);
            if (fail)
            {
                throw new IOException("storage unavailable"); // as a timeout looks: the write may still have landed
            }
        }

        public Task<Stream> OpenAsync(string blobName, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(string blobName, CancellationToken ct)
        {
            Deleted.Add(blobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A repository whose delete fails, as the database can while storage is failing too.</summary>
    private sealed class UndeletableRepo : IDocumentRepository
    {
        private readonly InMemoryRepo _inner = new();
        public Task CreateAsync(DocumentRecord document, CancellationToken ct) => _inner.CreateAsync(document, ct);
        public Task<IReadOnlyList<DocumentRecord>> ListAsync(string ownerId, CancellationToken ct) => _inner.ListAsync(ownerId, ct);
        public Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken ct) => throw new InvalidOperationException("database unavailable");
        public Task<DocumentRecord?> GetAsync(string ownerId, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task SetStatusAsync(Guid id, DocumentStatus status, string? error, CancellationToken ct) => throw new NotSupportedException();
        public Task SetIndexedAsync(Guid id, DocumentKind kind, int pageCount, int chunkCount, CancellationToken ct) => throw new NotSupportedException();
        public Task SavePagesAsync(Guid id, IReadOnlyList<AnalyzedPage> pages, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AnalyzedPage>> GetPagesAsync(string ownerId, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveInvoiceFieldsAsync(Guid id, InvoiceFields fields, string method, CancellationToken ct) => throw new NotSupportedException();
        public Task<(InvoiceFields Fields, string Method)?> GetInvoiceFieldsAsync(string ownerId, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Record_is_written_before_the_blob_and_the_message_after_it()
    {
        var repo = new InMemoryRepo();
        var store = new RecordingStore(repo);
        var queue = new InMemoryQueue();
        var record = await EndpointExtensions.StoreUploadAsync("gina", "INV-1.pdf", new MemoryStream([1, 2, 3]), 3, store, repo, queue, NullLogger.Instance, default);

        Assert.True(store.RecordExistedAtSave);
        Assert.Equal(DocumentStatus.Uploaded, repo.Docs[record.Id].Status);
        Assert.Equal($"gina/{record.Id}.pdf", record.BlobName);
        var queued = await queue.DequeueAsync(default);
        Assert.Equal(record.Id, queued!.Message.DocumentId);
    }

    [Fact]
    public async Task A_failed_blob_write_removes_the_blob_and_the_record_and_queues_nothing()
    {
        var repo = new InMemoryRepo();
        var queue = new InMemoryQueue();
        var store = new RecordingStore(repo, fail: true);
        await Assert.ThrowsAsync<IOException>(() =>
            EndpointExtensions.StoreUploadAsync("gina", "INV-1.pdf", new MemoryStream([1, 2, 3]), 3, store, repo, queue, NullLogger.Instance, default));

        Assert.Empty(repo.Docs);
        Assert.Equal([store.Saved!], store.Deleted);
        Assert.Null(await queue.DequeueAsync(default));
    }

    [Fact]
    public async Task A_failing_clean_up_does_not_hide_the_storage_error()
    {
        var repo = new UndeletableRepo();
        var store = new RecordingStore(repo, fail: true);
        var queue = new InMemoryQueue();
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            EndpointExtensions.StoreUploadAsync("gina", "INV-1.pdf", new MemoryStream([1, 2, 3]), 3, store, repo, queue, NullLogger.Instance, default));

        Assert.Equal("storage unavailable", ex.Message);
        Assert.Equal([store.Saved!], store.Deleted);
        Assert.Null(await queue.DequeueAsync(default));
    }
}
