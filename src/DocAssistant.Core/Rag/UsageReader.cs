using System.Reflection;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Pricing;

namespace DocAssistant.Core.Rag;

/// <summary>
/// Reads token usage from Semantic Kernel result metadata without depending on a connector's usage type
/// (OpenAI's ChatTokenUsage has InputTokenCount/OutputTokenCount; others use PromptTokens/CompletionTokens).
/// When nothing is reported, tokens are estimated at four characters each and flagged as estimated.
/// </summary>
public static class UsageReader
{
    public static UsageInfo FromMetadata(IReadOnlyDictionary<string, object?>? metadata, string model, ModelPricing pricing, string promptText, string completionText)
    {
        if (metadata is not null && metadata.TryGetValue("Usage", out var usage) && usage is not null)
        {
            var input = ReadInt(usage, "InputTokenCount", "PromptTokens", "InputTokens");
            var output = ReadInt(usage, "OutputTokenCount", "CompletionTokens", "OutputTokens");
            if (input is not null || output is not null)
            {
                return pricing.Usage(model, input ?? 0, output ?? 0);
            }
        }
        var estIn = Math.Max(1, promptText.Length / 4);
        var estOut = Math.Max(0, completionText.Length / 4);
        return pricing.Usage(model, estIn, estOut) with { Estimated = true };
    }

    private static int? ReadInt(object usage, params string[] names)
    {
        var type = usage.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(usage) is { } value)
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        if (usage is IReadOnlyDictionary<string, object?> dict)
        {
            foreach (var name in names)
            {
                if (dict.TryGetValue(name, out var v) && v is not null)
                {
                    return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        return null;
    }
}
