using System.Text.Json.Serialization;

namespace DocAssistant.Core.Domain;

/// <summary>Structured fields extracted from an invoice. Nulls mean "not found", never a guess.</summary>
public sealed record InvoiceFields(
    [property: JsonPropertyName("invoice_number")] string? InvoiceNumber,
    [property: JsonPropertyName("invoice_date")] string? InvoiceDate,
    [property: JsonPropertyName("due_date")] string? DueDate,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("customer")] string? Customer,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("subtotal")] decimal? Subtotal,
    [property: JsonPropertyName("tax")] decimal? Tax,
    [property: JsonPropertyName("total")] decimal? Total,
    [property: JsonPropertyName("payment_terms")] string? PaymentTerms,
    [property: JsonPropertyName("line_items")] IReadOnlyList<InvoiceLine> LineItems)
{
    public static readonly InvoiceFields Empty = new(null, null, null, null, null, null, null, null, null, null, []);
}

public sealed record InvoiceLine(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unit_price")] decimal UnitPrice,
    [property: JsonPropertyName("amount")] decimal Amount);

public sealed record ExtractionResult(InvoiceFields Fields, string Method, UsageInfo Usage, TimeSpan Elapsed);
