using DocAssistant.Core.Domain;
using DocAssistant.Core.Evals;
using DocAssistant.Core.Extraction;
using DocAssistant.Core.Ingestion;
using DocAssistant.Core.Pricing;
using DocAssistant.Core.Rag;

namespace DocAssistant.UnitTests;

public class TextChunkerTests
{
    private static readonly TextChunker Chunker = new() { MaxChars = 200, OverlapChars = 40, MinChars = 10 };

    [Fact]
    public void Short_page_is_one_chunk_and_whitespace_is_normalised()
    {
        var chunks = Chunker.Chunk(Guid.NewGuid(), "u", [new AnalyzedPage(1, "Hello\r\n\r\n\r\n\tworld  ")]);
        Assert.Single(chunks);
        Assert.Equal("Hello\n\nworld", chunks[0].Text);
        Assert.Equal(1, chunks[0].Page);
    }

    [Fact]
    public void Long_page_splits_under_budget_with_overlap_and_never_crosses_pages()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("word", 30)); // 149 chars
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 5));
        var docId = Guid.NewGuid();
        var chunks = Chunker.Chunk(docId, "u", [new AnalyzedPage(1, text), new AnalyzedPage(2, "second page")]);
        Assert.True(chunks.Count >= 5);
        Assert.All(chunks.Where(c => c.Page == 1), c => Assert.True(c.Text.Length <= Chunker.MaxChars + Chunker.OverlapChars + 2));
        Assert.Equal("second page", chunks.Last().Text);
        Assert.Equal(2, chunks.Last().Page);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        Assert.All(chunks, c => Assert.Equal(docId, c.DocumentId));
    }

    [Fact]
    public void Oversized_paragraph_is_hard_split_at_sentence_boundaries()
    {
        var text = string.Join(" ", Enumerable.Repeat("This is a sentence.", 40));
        var pieces = Chunker.SplitPage(text);
        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.True(p.Length <= Chunker.MaxChars + Chunker.OverlapChars + 2));
    }

    [Fact]
    public void Empty_page_produces_no_chunk()
    {
        Assert.Empty(Chunker.Chunk(Guid.NewGuid(), "u", [new AnalyzedPage(1, "   \n ")]));
    }
}

public class CitationsTests
{
    private static SearchHit Hit(string file, int page, string text) =>
        new(new Chunk(Guid.NewGuid(), Guid.NewGuid(), "u", page, 0, text), file, 0.5, null, null);

    [Fact]
    public void Sources_are_numbered_and_markers_parse_in_order_without_duplicates()
    {
        var hits = new[] { Hit("a.pdf", 1, "alpha text"), Hit("b.pdf", 3, "beta text"), Hit("c.pdf", 2, "gamma") };
        var block = Citations.FormatSources(hits);
        Assert.Contains("[1] a.pdf, page 1:\nalpha text", block, StringComparison.Ordinal);
        Assert.Contains("[3] c.pdf, page 2:", block, StringComparison.Ordinal);

        var cites = Citations.Parse("Beta says so [2]. Also alpha [1] and again [2], and nonsense [9].", hits);
        Assert.Equal([2, 1], cites.Select(c => c.Marker));
        Assert.Equal("b.pdf", cites[0].FileName);
        Assert.Equal(3, cites[0].Page);
        Assert.Equal(hits[0].Chunk.Id, cites[1].ChunkId);
    }

    [Fact]
    public void No_sources_renders_a_placeholder_and_snippets_are_bounded()
    {
        Assert.Equal("(no sources found)", Citations.FormatSources([]));
        var snippet = Citations.Snippet(new string('x', 500) + "\n\n  tail", 50);
        Assert.Equal(53, snippet.Length);
        Assert.EndsWith("...", snippet, StringComparison.Ordinal);
    }
}

public class RegexInvoiceExtractorTests
{
    private const string Sample = """
        INVOICE
        Invoice Number: INV-1007
        Invoice Date: 2026-08-14
        Due Date: September 13, 2026
        Payment Terms: Net 30
        From: Northwind Supply Co.
        Bill To: Harbor Clinic LLC
        Subtotal: $1,234.50
        Sales Tax (7%): $86.42
        Total Due: $1,320.92
        """;

    [Fact]
    public void Extracts_header_fields_amounts_and_normalises_dates()
    {
        var f = RegexInvoiceExtractor.Extract(Sample);
        Assert.Equal("INV-1007", f.InvoiceNumber);
        Assert.Equal("2026-08-14", f.InvoiceDate);
        Assert.Equal("2026-09-13", f.DueDate);
        Assert.Equal("Net 30", f.PaymentTerms);
        Assert.Equal("Northwind Supply Co.", f.Vendor);
        Assert.Equal("Harbor Clinic LLC", f.Customer);
        Assert.Equal(1234.50m, f.Subtotal);
        Assert.Equal(86.42m, f.Tax);
        Assert.Equal(1320.92m, f.Total);
        Assert.Equal("USD", f.Currency);
    }

    [Fact]
    public void Missing_fields_stay_null_instead_of_being_guessed()
    {
        var f = RegexInvoiceExtractor.Extract("Hello, this is not an invoice.");
        Assert.Null(f.InvoiceNumber);
        Assert.Null(f.Total);
        Assert.Null(f.Currency);
        Assert.Empty(f.LineItems);
    }

    [Theory]
    [InlineData("2026-01-05", "2026-01-05")]
    [InlineData("January 5, 2026", "2026-01-05")]
    [InlineData("1/5/2026", "2026-01-05")]
    [InlineData("someday", "someday")]
    public void Dates_normalise_when_recognised(string raw, string expected) =>
        Assert.Equal(expected, RegexInvoiceExtractor.Date(raw));
}

public class ScoringTests
{
    [Theory]
    [InlineData("1320.92", "The total due is $1,320.92 [1].", true)]
    [InlineData("1320.92", "The total is 1320.9 dollars", true)]
    [InlineData("1320.92", "The total is 1300 dollars", false)]
    [InlineData("1320.92", "The total is 1321", true)]
    [InlineData("60", "Sixty days? It says 60 days notice [2]", true)]
    [InlineData("2026-09-13", "It is due on September 13, 2026.", true)]
    [InlineData("2026-09-13", "It is due 9/13/2026.", true)]
    [InlineData("2026-09-13", "It is due in October.", false)]
    [InlineData("Northwind Supply Co.", "Issued by NORTHWIND SUPPLY CO [1]", true)]
    [InlineData("Florida", "Governed by the laws of the State of Georgia.", false)]
    public void Answer_matching(string expected, string answer, bool match) =>
        Assert.Equal(match, Scoring.AnswerMatches(expected, answer));

    [Fact]
    public void Extraction_agreement_counts_fields()
    {
        var truth = new InvoiceFields("INV-1", "2026-01-01", "2026-01-31", "V", "C", "USD", 100m, 7m, 107m, "Net 30", []);
        var got = truth with { Vendor = "v", Total = 107.004m, Customer = null };
        var (correct, total) = Scoring.ExtractionAgreement(truth, got);
        Assert.Equal(8, total);
        Assert.Equal(7, correct);
    }

    [Fact]
    public void Percentile_interpolates()
    {
        Assert.Equal(0, Scoring.Percentile([], 0.5));
        Assert.Equal(3, Scoring.Percentile([1, 2, 3, 4, 5], 0.5));
        Assert.Equal(4.8, Scoring.Percentile([1, 2, 3, 4, 5], 0.95), 6);
    }
}

public class ModelPricingTests
{
    [Fact]
    public void Prices_known_models_and_deployment_prefixes_and_refuses_to_invent()
    {
        var p = new ModelPricing();
        Assert.Equal(0.15m + 0.60m, p.Cost("gpt-4o-mini", 1_000_000, 1_000_000));
        Assert.Equal(p.Cost("gpt-4o-mini", 1000, 10), p.Cost("gpt-4o-mini-2024-07-18", 1000, 10));
        Assert.Equal(0m, p.Cost("qwen3.5:4b", 5000, 500));
        var over = new ModelPricing(new Dictionary<string, (decimal, decimal)> { ["gpt-4o-mini"] = (1m, 2m) });
        Assert.Equal(3m, over.Cost("gpt-4o-mini", 1_000_000, 1_000_000));
        Assert.True(p.Usage("gpt-4o", 10, 10).CostUsd > 0);
    }
}

public class UsageReaderTests
{
    private sealed class OpenAiLikeUsage
    {
        public int InputTokenCount { get; init; }
        public int OutputTokenCount { get; init; }
    }

    [Fact]
    public void Reads_provider_usage_from_objects_and_dictionaries()
    {
        var pricing = new ModelPricing();
        var fromObject = UsageReader.FromMetadata(new Dictionary<string, object?> { ["Usage"] = new OpenAiLikeUsage { InputTokenCount = 120, OutputTokenCount = 30 } }, "gpt-4o-mini", pricing, "", "");
        Assert.Equal((120, 30, false), (fromObject.InputTokens, fromObject.OutputTokens, fromObject.Estimated));
        var fromDict = UsageReader.FromMetadata(new Dictionary<string, object?> { ["Usage"] = new Dictionary<string, object?> { ["PromptTokens"] = 7, ["CompletionTokens"] = 3 } }, "x", pricing, "", "");
        Assert.Equal((7, 3), (fromDict.InputTokens, fromDict.OutputTokens));
    }

    [Fact]
    public void Estimates_and_flags_when_nothing_is_reported()
    {
        var u = UsageReader.FromMetadata(null, "gpt-4o-mini", new ModelPricing(), new string('a', 400), new string('b', 40));
        Assert.True(u.Estimated);
        Assert.Equal(100, u.InputTokens);
        Assert.Equal(10, u.OutputTokens);
        Assert.True(u.CostUsd > 0);
    }
}

public class LlmInvoiceExtractorParseTests
{
    [Fact]
    public void Parses_fenced_and_surrounded_json_and_normalises_dates()
    {
        var fenced = "```json\n{\"invoice_number\":\"INV-9\",\"invoice_date\":\"March 2, 2026\",\"total\":\"12.5\",\"line_items\":null}\n```";
        var f = LlmInvoiceExtractor.Parse(fenced);
        Assert.NotNull(f);
        Assert.Equal("INV-9", f.InvoiceNumber);
        Assert.Equal("2026-03-02", f.InvoiceDate);
        Assert.Equal(12.5m, f.Total);
        Assert.Empty(f.LineItems);
        Assert.NotNull(LlmInvoiceExtractor.Parse("Sure! Here it is: {\"invoice_number\":\"A\",\"line_items\":[]} hope that helps"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{not valid json}")]
    public void Garbage_returns_null(string? content) => Assert.Null(LlmInvoiceExtractor.Parse(content));
}

public class DocumentClassifierTests
{
    [Theory]
    [InlineData("INV-1.pdf", "whatever", DocumentKind.Invoice)]
    [InlineData("x.pdf", "INVOICE\nInvoice Number: 1", DocumentKind.Invoice)]
    [InlineData("MSA-a-b.pdf", "", DocumentKind.Contract)]
    [InlineData("x.pdf", "This Master Services Agreement is entered", DocumentKind.Contract)]
    [InlineData("Manual-DH.pdf", "", DocumentKind.Manual)]
    [InlineData("x.pdf", "Specifications\nModel: DH", DocumentKind.Manual)]
    [InlineData("x.pdf", "a poem", DocumentKind.Unknown)]
    public void Classifies_by_name_then_first_page(string name, string text, DocumentKind kind) =>
        Assert.Equal(kind, DocumentClassifier.Classify(name, [new AnalyzedPage(1, text)]));
}
