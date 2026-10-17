using DocAssistant.Core.Domain;

namespace DocAssistant.Core.Pricing;

/// <summary>
/// USD per million tokens, used for the per-request cost figure. Azure OpenAI list prices as of 2026-10
/// (pay-as-you-go, US regions); local models cost nothing. Override with Pricing:Models in configuration.
/// </summary>
public sealed class ModelPricing
{
    private readonly Dictionary<string, (decimal Input, decimal Output)> _prices;

    public static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> Defaults =
        new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o-mini"] = (0.15m, 0.60m),
            ["gpt-4o"] = (2.50m, 10.00m),
            ["gpt-4.1-mini"] = (0.40m, 1.60m),
            ["gpt-4.1"] = (2.00m, 8.00m),
            ["text-embedding-3-small"] = (0.02m, 0m),
            ["text-embedding-3-large"] = (0.13m, 0m),
        };

    public ModelPricing(IReadOnlyDictionary<string, (decimal Input, decimal Output)>? overrides = null)
    {
        _prices = new Dictionary<string, (decimal, decimal)>(Defaults, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
        {
            foreach (var (model, price) in overrides)
            {
                _prices[model] = price;
            }
        }
    }

    public decimal Cost(string model, int inputTokens, int outputTokens)
    {
        if (!_prices.TryGetValue(model, out var price))
        {
            // deployment names often carry a suffix; fall back to the longest known prefix
            var known = _prices.Keys.Where(k => model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length).FirstOrDefault();
            if (known is null)
            {
                return 0m; // local or unknown model: no price, no invented number
            }
            price = _prices[known];
        }
        return decimal.Round(inputTokens / 1_000_000m * price.Input + outputTokens / 1_000_000m * price.Output, 6);
    }

    public UsageInfo Usage(string model, int inputTokens, int outputTokens, int calls = 1) =>
        new(inputTokens, outputTokens, calls, Cost(model, inputTokens, outputTokens));
}
