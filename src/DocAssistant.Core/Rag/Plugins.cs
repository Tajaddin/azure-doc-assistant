using System.ComponentModel;
using System.Text.Json;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using Microsoft.SemanticKernel;

namespace DocAssistant.Core.Rag;

/// <summary>Per-request state shared by the plugins and the orchestrator: the numbered sources and the tool log.</summary>
public sealed class AskContext(string ownerId, Guid? documentId)
{
    public string OwnerId { get; } = ownerId;
    public Guid? DocumentId { get; } = documentId;
    public List<SearchHit> Sources { get; } = [];
    public List<string> ToolCalls { get; } = [];
    public UsageInfo ToolUsage { get; set; } = UsageInfo.Zero;
}

/// <summary>Lets the model fetch more passages. New hits are appended to the numbered sources so [n] stays citable.</summary>
public sealed class DocumentSearchPlugin(IDocumentIndex index, IEmbedder embedder, AskContext context)
{
    [KernelFunction("search_documents")]
    [Description("Search the user's documents for passages about a topic. Returns numbered passages to cite.")]
    public async Task<string> SearchAsync(
        [Description("What to look for, as a short natural-language query")] string query,
        CancellationToken ct = default)
    {
        context.ToolCalls.Add($"search_documents({query})");
        var vector = (await embedder.EmbedAsync([query], ct))[0];
        var hits = await index.SearchAsync(new SearchRequest(context.OwnerId, query, vector, Top: 5, DocumentId: context.DocumentId), ct);
        var fresh = hits.Where(h => context.Sources.All(s => s.Chunk.Id != h.Chunk.Id)).ToList();
        if (fresh.Count == 0)
        {
            return "No new passages found.";
        }
        var start = context.Sources.Count;
        context.Sources.AddRange(fresh);
        return string.Join("\n\n", fresh.Select((h, i) =>
            $"[{start + i + 1}] {h.FileName}, page {h.Chunk.Page}:\n{Citations.Snippet(h.Chunk.Text, 800)}"));
    }
}

/// <summary>Structured invoice access: the "look up invoice total" function-calling path.</summary>
public sealed class InvoicePlugin(IDocumentRepository documents, IInvoiceExtractor extractor, AskContext context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    [KernelFunction("list_documents")]
    [Description("List the user's uploaded documents with their type and page count.")]
    public async Task<string> ListAsync(CancellationToken ct = default)
    {
        context.ToolCalls.Add("list_documents()");
        var docs = await documents.ListAsync(context.OwnerId, ct);
        if (docs.Count == 0)
        {
            return "The user has no documents.";
        }
        return string.Join("\n", docs.Select(d => $"- {d.FileName} ({d.Kind}, {d.PageCount} pages, {d.Status})"));
    }

    [KernelFunction("lookup_invoice")]
    [Description("Return the structured fields of an invoice (number, dates, vendor, customer, subtotal, tax, total, line items) by file name.")]
    public async Task<string> LookupAsync(
        [Description("The invoice's file name, for example INV-1007.pdf")] string fileName,
        CancellationToken ct = default)
    {
        context.ToolCalls.Add($"lookup_invoice({fileName})");
        var docs = await documents.ListAsync(context.OwnerId, ct);
        var doc = docs.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                  ?? docs.FirstOrDefault(d => d.FileName.Contains(fileName, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            return $"No document named {fileName}.";
        }
        var stored = await documents.GetInvoiceFieldsAsync(context.OwnerId, doc.Id, ct);
        InvoiceFields fields;
        if (stored is not null)
        {
            fields = stored.Value.Fields;
        }
        else
        {
            var pages = await documents.GetPagesAsync(context.OwnerId, doc.Id, ct);
            var result = await extractor.ExtractAsync(pages, context.OwnerId, ct);
            context.ToolUsage = context.ToolUsage.Add(result.Usage);
            await documents.SaveInvoiceFieldsAsync(doc.Id, result.Fields, result.Method, ct);
            fields = result.Fields;
        }
        return JsonSerializer.Serialize(new { file = doc.FileName, fields }, Json);
    }
}
