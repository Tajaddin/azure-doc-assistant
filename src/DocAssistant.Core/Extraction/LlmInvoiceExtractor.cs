using System.Diagnostics;
using System.Text.Json;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Pricing;
using DocAssistant.Core.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DocAssistant.Core.Extraction;

/// <summary>
/// Invoice fields through the model with a JSON response format. If the model's JSON cannot be parsed the
/// regex baseline is used and the method is recorded as "regex-fallback", so the evals can count it.
/// </summary>
public sealed class LlmInvoiceExtractor(
    Kernel kernel,
    ModelPricing pricing,
    string modelName,
    ICostLedger costs,
    ILogger<LlmInvoiceExtractor> log) : IInvoiceExtractor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
    public const int MaxChars = 12_000;

    public string Method => "llm";

    public async Task<ExtractionResult> ExtractAsync(IReadOnlyList<AnalyzedPage> pages, string ownerId, CancellationToken ct)
    {
        using var span = AskService.Activity.StartActivity("extract");
        var sw = Stopwatch.StartNew();
        var text = string.Join("\n\n", pages.Select(p => $"--- page {p.Number} ---\n{p.Text}"));
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars];
        }
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory(Prompts.ExtractSystem);
        history.AddUserMessage(text);
        var settings = new OpenAIPromptExecutionSettings { Temperature = 0, MaxTokens = 900, ResponseFormat = "json_object" };
        var result = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
        var usage = UsageReader.FromMetadata(result.Metadata, modelName, pricing, Prompts.ExtractSystem + text, result.Content ?? string.Empty);
        await costs.RecordAsync(new CostEntry(Guid.NewGuid(), ownerId, "extract", modelName, usage.InputTokens, usage.OutputTokens, usage.CostUsd, DateTimeOffset.UtcNow), CancellationToken.None);

        var fields = Parse(result.Content);
        if (fields is null)
        {
            log.LogWarning("model returned unparsable JSON for extraction; falling back to regex");
            fields = RegexInvoiceExtractor.Extract(string.Join("\n", pages.Select(p => p.Text)));
            return new ExtractionResult(fields, "regex-fallback", usage, sw.Elapsed);
        }
        span?.SetTag("fields.total", fields.Total);
        return new ExtractionResult(fields, Method, usage, sw.Elapsed);
    }

    public static InvoiceFields? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }
        var s = content.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var first = s.IndexOf('\n');
            s = first > 0 ? s[(first + 1)..] : s;
            var fence = s.LastIndexOf("```", StringComparison.Ordinal);
            s = fence > 0 ? s[..fence] : s;
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<InvoiceFields>(s[start..(end + 1)], Json);
            return parsed is null ? null : parsed with
            {
                InvoiceDate = RegexInvoiceExtractor.Date(parsed.InvoiceDate),
                DueDate = RegexInvoiceExtractor.Date(parsed.DueDate),
                LineItems = parsed.LineItems ?? [],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
