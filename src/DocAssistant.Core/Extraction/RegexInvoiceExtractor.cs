using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;

namespace DocAssistant.Core.Extraction;

/// <summary>
/// Pattern-based invoice extraction: the no-model baseline the LLM extractor is compared against in the evals,
/// and the fallback when no model is configured. It understands the common "Label: value" layouts; it does
/// not understand tables of line items beyond simple "description qty price amount" rows.
/// </summary>
public sealed partial class RegexInvoiceExtractor : IInvoiceExtractor
{
    public string Method => "regex";

    public Task<ExtractionResult> ExtractAsync(IReadOnlyList<AnalyzedPage> pages, string ownerId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var text = string.Join("\n", pages.Select(p => p.Text));
        var fields = Extract(text);
        return Task.FromResult(new ExtractionResult(fields, Method, UsageInfo.Zero, sw.Elapsed));
    }

    public static InvoiceFields Extract(string text)
    {
        return new InvoiceFields(
            InvoiceNumber: First(InvoiceNumber(), text),
            InvoiceDate: Date(First(InvoiceDate(), text)),
            DueDate: Date(First(DueDate(), text)),
            Vendor: First(Vendor(), text),
            Customer: First(Customer(), text),
            Currency: text.Contains('$') || text.Contains("USD", StringComparison.OrdinalIgnoreCase) ? "USD" : null,
            Subtotal: Money(First(Subtotal(), text)),
            Tax: Money(First(Tax(), text)),
            Total: Money(First(Total(), text)),
            PaymentTerms: First(Terms(), text),
            LineItems: Lines(text));
    }

    [GeneratedRegex(@"invoice\s*(?:number|no\.?|#)\s*[:#]?\s*([A-Z0-9][A-Z0-9-]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceNumber();

    [GeneratedRegex(@"(?:invoice\s+date|date\s+of\s+issue|issued)\s*[:]?\s*(\d{4}-\d{2}-\d{2}|[A-Za-z]+\s+\d{1,2},\s+\d{4}|\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceDate();

    [GeneratedRegex(@"(?:due\s+date|payment\s+due|due)\s*[:]?\s*(\d{4}-\d{2}-\d{2}|[A-Za-z]+\s+\d{1,2},\s+\d{4}|\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DueDate();

    [GeneratedRegex(@"(?:^|\n)\s*(?:from|vendor|supplier|seller|billed\s+by)\s*[:]\s*([^\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Vendor();

    [GeneratedRegex(@"(?:^|\n)\s*(?:bill\s+to|customer|client|sold\s+to|billed\s+to)\s*[:]\s*([^\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Customer();

    [GeneratedRegex(@"sub\s*total\s*[:]?\s*\$?\s*([\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Subtotal();

    [GeneratedRegex(@"(?:sales\s+)?tax(?:\s*\([^)]*\))?\s*[:]?\s*\$?\s*([\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Tax();

    [GeneratedRegex(@"(?:amount\s+due|total\s+due|grand\s+total|(?<!sub\s?)total)\s*[:]?\s*\$?\s*([\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Total();

    [GeneratedRegex(@"(?:payment\s+)?terms\s*[:]?\s*((?:net\s*\d+|due\s+on\s+receipt)[^\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Terms();

    [GeneratedRegex(@"(?m)^(?<desc>[A-Za-z][A-Za-z0-9 ,'&()/-]{2,60}?)\s{2,}(?<qty>\d+(?:\.\d+)?)\s{2,}\$?(?<price>[\d,]+\.\d{2})\s{2,}\$?(?<amount>[\d,]+\.\d{2})\s*$")]
    private static partial Regex LineRow();

    private static string? First(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static decimal? Money(string? raw) =>
        raw is not null && decimal.TryParse(raw.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static string? Date(string? raw)
    {
        if (raw is null)
        {
            return null;
        }
        string[] formats = ["yyyy-MM-dd", "MMMM d, yyyy", "MMM d, yyyy", "M/d/yyyy", "MM/dd/yyyy"];
        return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : raw.Trim();
    }

    private static IReadOnlyList<InvoiceLine> Lines(string text)
    {
        var lines = new List<InvoiceLine>();
        foreach (Match m in LineRow().Matches(text))
        {
            var desc = m.Groups["desc"].Value.Trim();
            if (desc.Contains("subtotal", StringComparison.OrdinalIgnoreCase) || desc.Contains("total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            lines.Add(new InvoiceLine(
                desc,
                decimal.Parse(m.Groups["qty"].Value, CultureInfo.InvariantCulture),
                decimal.Parse(m.Groups["price"].Value.Replace(",", ""), CultureInfo.InvariantCulture),
                decimal.Parse(m.Groups["amount"].Value.Replace(",", ""), CultureInfo.InvariantCulture)));
        }
        return lines;
    }
}
