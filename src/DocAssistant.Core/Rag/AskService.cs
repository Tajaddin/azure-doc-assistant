using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Pricing;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DocAssistant.Core.Rag;

public sealed record AskEvent(string Type, string? Text = null, object? Data = null);

/// <summary>
/// Retrieval-augmented answering with Semantic Kernel: hybrid retrieval, a numbered-source prompt, and a
/// bounded manual function-calling loop (search_documents, lookup_invoice, list_documents). The loop is
/// manual rather than auto-invoke so every model round is accounted for (tokens, cost, span) and capped.
/// </summary>
public sealed class AskService(
    Kernel kernel,
    IEmbedder embedder,
    IDocumentIndex index,
    IDocumentRepository documents,
    IInvoiceExtractor extractor,
    ICostLedger costs,
    ModelPricing pricing,
    string modelName,
    ILogger<AskService> log)
{
    public const int MaxRounds = 4;
    public const int TopK = 8;

    public static readonly ActivitySource Activity = new("DocAssistant");
    public static readonly Meter Meter = new("DocAssistant");
    private static readonly Histogram<double> AskDuration = Meter.CreateHistogram<double>("docassistant.ask.duration", "s");
    private static readonly Counter<long> AskCount = Meter.CreateCounter<long>("docassistant.ask.count");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("docassistant.tokens");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>("docassistant.cost_usd");
    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("docassistant.tool_calls");

    public async Task<Answer> AskAsync(string ownerId, string question, Guid? documentId, CancellationToken ct)
    {
        Answer? final = null;
        await foreach (var ev in StreamAsync(ownerId, question, documentId, ct))
        {
            if (ev.Type == "done" && ev.Data is Answer a)
            {
                final = a;
            }
        }
        return final ?? throw new InvalidOperationException("the model produced no answer");
    }

    public async IAsyncEnumerable<AskEvent> StreamAsync(
        string ownerId, string question, Guid? documentId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var span = Activity.StartActivity("ask");
        var parent = span?.Context ?? default;
        span?.SetTag("owner.hash", ownerId.GetHashCode());
        span?.SetTag("question.chars", question.Length);
        var sw = Stopwatch.StartNew();
        var context = new AskContext(ownerId, documentId);
        var usage = UsageInfo.Zero;

        // 1. retrieve
        using (var retrieve = Activity.StartActivity("ask.retrieve", ActivityKind.Internal, parent))
        {
            var vector = (await embedder.EmbedAsync([question], ct))[0];
            var hits = await index.SearchAsync(new SearchRequest(ownerId, question, vector, TopK, documentId), ct);
            context.Sources.AddRange(hits);
            retrieve?.SetTag("hits", hits.Count);
        }
        yield return new AskEvent("sources", Data: context.Sources.Select(SourceView).ToList());

        // 2. a per-request kernel with owner-scoped plugins
        var requestKernel = kernel.Clone();
        requestKernel.Plugins.AddFromObject(new DocumentSearchPlugin(index, embedder, context), "documents");
        requestKernel.Plugins.AddFromObject(new InvoicePlugin(documents, extractor, context), "invoices");
        var chat = requestKernel.GetRequiredService<IChatCompletionService>();
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = 700,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
        };
        var history = new ChatHistory(Prompts.AnswerSystem.Replace("{{sources}}", Citations.FormatSources(context.Sources)));
        history.AddUserMessage(question);

        // 3. bounded tool loop, streaming the text of every round
        var answer = new StringBuilder();
        var status = "answered";
        for (var round = 1; round <= MaxRounds; round++)
        {
            using var roundSpan = Activity.StartActivity("ask.model.round", ActivityKind.Internal, parent);
            roundSpan?.SetTag("round", round);
            var builder = new FunctionCallContentBuilder();
            var roundText = new StringBuilder();
            IReadOnlyDictionary<string, object?>? lastMeta = null;
            await foreach (var update in chat.GetStreamingChatMessageContentsAsync(history, settings, requestKernel, ct))
            {
                if (!string.IsNullOrEmpty(update.Content))
                {
                    roundText.Append(update.Content);
                    yield return new AskEvent("token", update.Content);
                }
                builder.Append(update);
                if (update.Metadata is not null && update.Metadata.ContainsKey("Usage"))
                {
                    lastMeta = update.Metadata;
                }
            }
            var promptChars = string.Join("\n", history.Select(m => m.Content ?? string.Empty));
            var roundUsage = UsageReader.FromMetadata(lastMeta, modelName, pricing, promptChars, roundText.ToString());
            usage = usage.Add(roundUsage);
            roundSpan?.SetTag("tokens.in", roundUsage.InputTokens);
            roundSpan?.SetTag("tokens.out", roundUsage.OutputTokens);

            var calls = builder.Build();
            if (calls.Count == 0)
            {
                answer.Append(roundText);
                break;
            }
            if (round == MaxRounds)
            {
                status = "tool_limit";
                answer.Append(roundText.Length > 0 ? roundText.ToString() : "I could not finish answering within the tool-call limit.");
                break;
            }
            var toolMessage = new ChatMessageContent(AuthorRole.Assistant, content: null);
            history.Add(toolMessage);
            foreach (var call in calls)
            {
                toolMessage.Items.Add(call);
                using var toolSpan = Activity.StartActivity("ask.tool", ActivityKind.Internal, roundSpan?.Context ?? parent);
                toolSpan?.SetTag("tool", call.FunctionName);
                ToolCalls.Add(1, new KeyValuePair<string, object?>("tool", call.FunctionName));
                yield return new AskEvent("tool", call.FunctionName, call.Arguments);
                FunctionResultContent result;
                try
                {
                    result = await call.InvokeAsync(requestKernel, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "tool {Tool} failed", call.FunctionName);
                    result = new FunctionResultContent(call, $"error: {ex.Message}");
                }
                history.Add(result.ToChatMessage());
            }
            // the sources may have grown; refresh the system prompt so new numbers are citable
            history[0] = new ChatMessageContent(AuthorRole.System, Prompts.AnswerSystem.Replace("{{sources}}", Citations.FormatSources(context.Sources)));
        }

        usage = usage.Add(context.ToolUsage);
        var text = answer.ToString().Trim();
        var citations = Citations.Parse(text, context.Sources);
        if (context.Sources.Count == 0)
        {
            status = "no_sources";
        }
        sw.Stop();
        var final = new Answer(text, citations, context.Sources, context.ToolCalls, usage, sw.Elapsed, modelName);

        await costs.RecordAsync(new CostEntry(Guid.NewGuid(), ownerId, "ask", modelName, usage.InputTokens, usage.OutputTokens, usage.CostUsd, DateTimeOffset.UtcNow), CancellationToken.None);
        AskDuration.Record(sw.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("status", status));
        AskCount.Add(1, new KeyValuePair<string, object?>("status", status), new KeyValuePair<string, object?>("cited", citations.Count > 0));
        Tokens.Add(usage.InputTokens, new KeyValuePair<string, object?>("direction", "input"));
        Tokens.Add(usage.OutputTokens, new KeyValuePair<string, object?>("direction", "output"));
        Cost.Add((double)usage.CostUsd);
        span?.SetTag("status", status);
        span?.SetTag("citations", citations.Count);
        span?.SetTag("tool_calls", context.ToolCalls.Count);
        log.LogInformation(
            "ask done status={Status} ms={Ms} sources={Sources} citations={Citations} tools={Tools} tokens_in={In} tokens_out={Out} cost_usd={Cost} estimated={Estimated} model={Model}",
            status, (int)sw.Elapsed.TotalMilliseconds, context.Sources.Count, citations.Count, context.ToolCalls.Count,
            usage.InputTokens, usage.OutputTokens, usage.CostUsd, usage.Estimated, modelName);
        yield return new AskEvent("done", Data: final);
    }

    public static object SourceView(SearchHit h, int i) => new
    {
        marker = i + 1,
        documentId = h.Chunk.DocumentId,
        fileName = h.FileName,
        page = h.Chunk.Page,
        chunkId = h.Chunk.Id,
        score = Math.Round(h.Score, 4),
        snippet = Citations.Snippet(h.Chunk.Text),
    };
}
