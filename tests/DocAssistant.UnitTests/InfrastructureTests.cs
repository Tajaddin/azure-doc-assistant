using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DocAssistant.Api.Auth;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Extraction;
using DocAssistant.Core.Pricing;
using DocAssistant.Core.Rag;
using DocAssistant.Infrastructure.Ai;
using DocAssistant.Infrastructure.Analysis;
using DocAssistant.SynthData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DocAssistant.UnitTests;

public class HashEmbeddingGeneratorTests
{
    [Fact]
    public async Task Deterministic_unit_length_and_lexically_sensitive()
    {
        var gen = new HashEmbeddingGenerator(256);
        var a = (await gen.GenerateAsync(["invoice total due amount"]))[0].Vector.ToArray();
        var again = (await gen.GenerateAsync(["invoice total due amount"]))[0].Vector.ToArray();
        var similar = (await gen.GenerateAsync(["the invoice total amount"]))[0].Vector.ToArray();
        var other = (await gen.GenerateAsync(["dehumidifier error code"]))[0].Vector.ToArray();
        Assert.Equal(a, again);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => x * x)), 3);
        Assert.True(Dot(a, similar) > Dot(a, other));
    }

    private static double Dot(float[] x, float[] y) => x.Zip(y).Sum(p => p.First * p.Second);
}

public class FakeChatCompletionServiceTests
{
    [Fact]
    public async Task Quotes_the_first_source_and_cites_it()
    {
        var svc = new FakeChatCompletionService();
        var history = new ChatHistory(Prompts.AnswerSystem.Replace("{{sources}}", "[1] INV-1.pdf, page 1:\nTotal Due: $10.00\nmore"));
        history.AddUserMessage("total?");
        var reply = await svc.GetChatMessageContentAsync(history);
        Assert.Contains("Total Due: $10.00 [1]", reply.Content, StringComparison.Ordinal);
        Assert.NotNull(reply.Metadata);
        Assert.True(reply.Metadata!.ContainsKey("Usage"));
    }

    [Fact]
    public async Task Declines_without_sources_and_streams_the_same_text()
    {
        var svc = new FakeChatCompletionService();
        var history = new ChatHistory(Prompts.AnswerSystem.Replace("{{sources}}", "(no sources found)"));
        history.AddUserMessage("anything");
        var reply = await svc.GetChatMessageContentAsync(history);
        Assert.Contains("do not contain", reply.Content, StringComparison.Ordinal);
        var streamed = new System.Text.StringBuilder();
        await foreach (var chunk in svc.GetStreamingChatMessageContentsAsync(history))
        {
            streamed.Append(chunk.Content);
        }
        Assert.Equal(reply.Content, streamed.ToString().Trim());
    }

    [Fact]
    public async Task Extraction_prompt_yields_json()
    {
        var svc = new FakeChatCompletionService();
        var history = new ChatHistory(Prompts.ExtractSystem);
        history.AddUserMessage("Invoice Number: INV-5\nTotal Due: $20.00");
        var reply = await svc.GetChatMessageContentAsync(history);
        var fields = LlmInvoiceExtractor.Parse(reply.Content);
        Assert.NotNull(fields);
        Assert.Equal("INV-5", fields.InvoiceNumber);
        Assert.Equal(20m, fields.Total);
    }
}

public class DevTokenTests
{
    [Fact]
    public void Tokens_validate_carry_a_stable_oid_and_owner_id_prefers_oid()
    {
        var options = new DevAuthOptions { SigningKey = "unit-test-signing-key-0123456789abcdef" };
        var issuer = new DevTokenIssuer(options);
        var token = issuer.Issue("alice");
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = issuer.Key,
        }, out _);
        Assert.Equal(DevTokenIssuer.DeterministicId("alice").ToString(), principal.FindFirstValue("oid"));
        Assert.Equal(DevTokenIssuer.DeterministicId("ALICE"), DevTokenIssuer.DeterministicId("alice"));
        Assert.NotEqual(DevTokenIssuer.DeterministicId("alice"), DevTokenIssuer.DeterministicId("bob"));
        Assert.Equal(principal.FindFirstValue("oid"), principal.OwnerId());

        var entraStyle = new ClaimsPrincipal(new ClaimsIdentity([new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "entra-oid"), new Claim("sub", "subject")]));
        Assert.Equal("entra-oid", entraStyle.OwnerId());
        Assert.Throws<InvalidOperationException>(() => new ClaimsPrincipal(new ClaimsIdentity()).OwnerId());
    }
}

public class PdfPigAnalyzerTests
{
    [Fact]
    public async Task Reads_synthetic_invoice_text_in_order()
    {
        var corpus = SyntheticCorpus.Generate(invoices: 1, contracts: 0, manuals: 1);
        var inv = corpus.Invoices[0];
        var analyzer = new PdfPigAnalyzer();
        var doc = await analyzer.AnalyzeAsync(new MemoryStream(SyntheticCorpus.RenderInvoice(inv)), inv.FileName, CancellationToken.None);
        Assert.Equal(1, doc.PageCount);
        Assert.Contains($"Invoice Number: {inv.Number}", doc.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Total Due:", doc.Pages[0].Text, StringComparison.Ordinal);

        var fields = RegexInvoiceExtractor.Extract(doc.Pages[0].Text);
        Assert.Equal(inv.Number, fields.InvoiceNumber);
        Assert.Equal(inv.Total, fields.Total);
        Assert.Equal(inv.Vendor, fields.Vendor);
        Assert.Equal(inv.DueDate, fields.DueDate);

        var manual = await analyzer.AnalyzeAsync(new MemoryStream(SyntheticCorpus.RenderManual(corpus.Manuals[0])), "m.pdf", CancellationToken.None);
        Assert.Equal(2, manual.PageCount);
        Assert.Contains("Error code E2", manual.Pages[1].Text, StringComparison.Ordinal);
    }
}

/// <summary>The orchestrator with the fake model and in-memory ports: prompt assembly, citations, cost accounting.</summary>
public class AskServiceTests
{
    private sealed class MemoryIndex(IEmbedder embedder) : IDocumentIndex
    {
        public readonly List<(Chunk Chunk, float[] Vector, string File)> Items = [];
        public Task UpsertAsync(IReadOnlyList<(Chunk Chunk, float[] Vector)> items, CancellationToken ct)
        {
            Items.AddRange(items.Select(i => (i.Chunk, i.Vector, "doc.pdf")));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest r, CancellationToken ct)
        {
            IReadOnlyList<SearchHit> hits = Items.Where(i => i.Chunk.OwnerId == r.OwnerId)
                .Select(i => new SearchHit(i.Chunk, i.File, i.Vector.Zip(r.QueryVector).Sum(p => p.First * p.Second), null, null))
                .OrderByDescending(h => h.Score).Take(r.Top).ToList();
            return Task.FromResult(hits);
        }
        public Task DeleteDocumentAsync(Guid documentId, CancellationToken ct) { Items.RemoveAll(i => i.Chunk.DocumentId == documentId); return Task.CompletedTask; }
    }

    private sealed class MemoryLedger : ICostLedger
    {
        public readonly List<CostEntry> Entries = [];
        public Task RecordAsync(CostEntry entry, CancellationToken ct) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<CostSummary> SummaryAsync(string ownerId, DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class EmptyRepo : IDocumentRepository
    {
        public Task CreateAsync(DocumentRecord document, CancellationToken ct) => Task.CompletedTask;
        public Task<DocumentRecord?> GetAsync(string ownerId, Guid id, CancellationToken ct) => Task.FromResult<DocumentRecord?>(null);
        public Task<IReadOnlyList<DocumentRecord>> ListAsync(string ownerId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DocumentRecord>>([]);
        public Task SetStatusAsync(Guid id, DocumentStatus status, string? error, CancellationToken ct) => Task.CompletedTask;
        public Task SetIndexedAsync(Guid id, DocumentKind kind, int pageCount, int chunkCount, CancellationToken ct) => Task.CompletedTask;
        public Task SavePagesAsync(Guid id, IReadOnlyList<AnalyzedPage> pages, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AnalyzedPage>> GetPagesAsync(string ownerId, Guid id, CancellationToken ct) => Task.FromResult<IReadOnlyList<AnalyzedPage>>([]);
        public Task SaveInvoiceFieldsAsync(Guid id, InvoiceFields fields, string method, CancellationToken ct) => Task.CompletedTask;
        public Task<(InvoiceFields Fields, string Method)?> GetInvoiceFieldsAsync(string ownerId, Guid id, CancellationToken ct) => Task.FromResult<(InvoiceFields, string)?>(null);
        public Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken ct) => Task.FromResult(false);
    }

    private static (AskService Service, MemoryIndex Index, MemoryLedger Ledger, IEmbedder Embedder) Build()
    {
        var options = new AiOptions { Provider = "fake" };
        var kernel = KernelFactory.Create(options);
        var embedder = KernelFactory.CreateEmbedder(kernel, options);
        var index = new MemoryIndex(embedder);
        var ledger = new MemoryLedger();
        var svc = new AskService(kernel, embedder, index, new EmptyRepo(), new RegexInvoiceExtractor(), ledger, new ModelPricing(), "fake", NullLogger<AskService>.Instance);
        return (svc, index, ledger, embedder);
    }

    [Fact]
    public async Task Answers_with_a_citation_from_the_owners_documents_and_records_cost()
    {
        var (svc, index, ledger, embedder) = Build();
        var docId = Guid.NewGuid();
        var chunk = new Chunk(Guid.NewGuid(), docId, "alice", 2, 0, "Total Due: $1,320.92\nPayment Terms: Net 30");
        var other = new Chunk(Guid.NewGuid(), Guid.NewGuid(), "bob", 1, 0, "Total Due: $999.00");
        await index.UpsertAsync([(chunk, (await embedder.EmbedAsync([chunk.Text], default))[0]), (other, (await embedder.EmbedAsync([other.Text], default))[0])], default);

        var answer = await svc.AskAsync("alice", "What is the total due?", null, default);
        Assert.Contains("1,320.92", answer.Text, StringComparison.Ordinal);
        var cite = Assert.Single(answer.Citations);
        Assert.Equal((docId, 2, 1), (cite.DocumentId, cite.Page, cite.Marker));
        Assert.Single(answer.Sources); // bob's chunk never appears
        Assert.Empty(answer.ToolCalls);
        Assert.Equal(1, answer.Usage.Calls);
        Assert.True(answer.Usage.InputTokens > 0);
        Assert.Equal("ask", Assert.Single(ledger.Entries).Operation);
        Assert.Equal("fake", answer.Model);
    }

    [Fact]
    public async Task Without_documents_the_answer_declines_and_has_no_citations()
    {
        var (svc, _, _, _) = Build();
        var events = new List<AskEvent>();
        await foreach (var ev in svc.StreamAsync("nobody", "Anything?", null, default))
        {
            events.Add(ev);
        }
        Assert.Equal("sources", events[0].Type);
        Assert.Contains(events, e => e.Type == "token");
        var done = Assert.IsType<Answer>(events[^1].Data);
        Assert.Empty(done.Citations);
        Assert.Contains("do not contain", done.Text, StringComparison.Ordinal);
    }
}
