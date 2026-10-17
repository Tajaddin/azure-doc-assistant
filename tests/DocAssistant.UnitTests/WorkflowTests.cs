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
