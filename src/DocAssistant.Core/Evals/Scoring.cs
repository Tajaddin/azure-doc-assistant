using System.Globalization;
using System.Text.RegularExpressions;
using DocAssistant.Core.Domain;

namespace DocAssistant.Core.Evals;

/// <summary>Deterministic scoring shared by the eval runner and its tests. No model grades a model here.</summary>
public static partial class Scoring
{
    [GeneratedRegex(@"-?\d[\d,]*(?:\.\d+)?")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"[^a-z0-9 ]+")]
    private static partial Regex NonAlnum();

    public static string Normalize(string s) =>
        Regex.Replace(NonAlnum().Replace(s.ToLowerInvariant(), " "), @"\s+", " ").Trim();

    /// <summary>
    /// True when the expected value is in the answer: numbers within 0.5% (or 0.01 absolute), dates in any of
    /// the common renderings, otherwise a normalized substring match.
    /// </summary>
    public static bool AnswerMatches(string expected, string answer)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }
        if (decimal.TryParse(expected.Replace(",", "").TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            foreach (Match m in Numbers().Matches(answer))
            {
                if (decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var got))
                {
                    var tolerance = Math.Max(0.01m, Math.Abs(number) * 0.005m);
                    if (Math.Abs(got - number) <= tolerance)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        if (DateTime.TryParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var renderings = new[]
            {
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
                date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
                date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            };
            var normalizedAnswer = Normalize(answer);
            return renderings.Any(r => normalizedAnswer.Contains(Normalize(r)));
        }
        return Normalize(answer).Contains(Normalize(expected));
    }

    public static bool CitesExpectedDocument(IReadOnlyList<Citation> citations, string expectedFileName) =>
        citations.Any(c => string.Equals(c.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Field-level agreement between extracted invoice fields and the ground truth.</summary>
    public static (int Correct, int Total) ExtractionAgreement(InvoiceFields truth, InvoiceFields got)
    {
        var checks = new List<bool>
        {
            Same(truth.InvoiceNumber, got.InvoiceNumber),
            Same(truth.InvoiceDate, got.InvoiceDate),
            Same(truth.DueDate, got.DueDate),
            Same(truth.Vendor, got.Vendor),
            Same(truth.Customer, got.Customer),
            Close(truth.Subtotal, got.Subtotal),
            Close(truth.Tax, got.Tax),
            Close(truth.Total, got.Total),
        };
        return (checks.Count(c => c), checks.Count);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    private static bool Close(decimal? a, decimal? b) =>
        a is not null && b is not null && Math.Abs(a.Value - b.Value) <= 0.01m;

    public static double Percentile(IReadOnlyList<double> values, double pct)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var k = (ordered.Length - 1) * pct;
        var lo = (int)Math.Floor(k);
        var hi = (int)Math.Ceiling(k);
        return lo == hi ? ordered[lo] : ordered[lo] + (ordered[hi] - ordered[lo]) * (k - lo);
    }
}
