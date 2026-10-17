using System.Globalization;
using System.Text.Json.Serialization;
using DocAssistant.Core.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DocAssistant.SynthData;

/// <summary>
/// A deterministic synthetic corpus: invoices, contracts and manuals with every fact known, so the eval
/// questions are grounded by construction. Nothing here is a real company, person or transaction.
/// </summary>
public static class SyntheticCorpus
{
    static SyntheticCorpus()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public sealed record Invoice(
        string FileName, string Number, string Vendor, string Customer, string InvoiceDate, string DueDate, string Terms,
        IReadOnlyList<InvoiceLine> Lines, decimal Subtotal, decimal Tax, decimal Total)
    {
        public InvoiceFields Truth => new(Number, InvoiceDate, DueDate, Vendor, Customer, "USD", Subtotal, Tax, Total, Terms, Lines);
    }

    public sealed record Contract(
        string FileName, string Provider, string Client, string EffectiveDate, int TermMonths, string PaymentTerms,
        int TerminationNoticeDays, string GoverningLaw, decimal LiabilityCapUsd, decimal SlaPercent, decimal MonthlyFeeUsd);

    public sealed record Manual(
        string FileName, string Product, string Model, int PowerWatts, int CapacityPintsPerDay, int MinTempC, int MaxTempC,
        int FilterMonths, int WarrantyYears, decimal WeightKg, IReadOnlyList<ErrorCode> ErrorCodes);

    public sealed record ErrorCode(string Code, string Meaning);

    public sealed record EvalQuestion(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("question")] string Question,
        [property: JsonPropertyName("expected")] string? Expected,
        [property: JsonPropertyName("expected_file")] string? ExpectedFile,
        [property: JsonPropertyName("accept_any")] IReadOnlyList<string>? AcceptAny = null);

    public sealed record Corpus(IReadOnlyList<Invoice> Invoices, IReadOnlyList<Contract> Contracts, IReadOnlyList<Manual> Manuals);

    private static readonly string[] Vendors =
    [
        "Northwind Supply Co.", "Suncoast Office Products", "Bayline Logistics LLC", "Palmetto Technical Services",
        "Gulfstream Paper Company", "Orange Grove Catering", "Hillsborough Cleaning Services", "Tampa Bay Printworks",
    ];

    private static readonly string[] Customers =
    [
        "Acme Retail Group", "Harbor Clinic LLC", "Bright Path Schools", "Cypress Point Hotels", "Meridian Dental Partners",
    ];

    private static readonly (string Description, decimal UnitPrice)[] Items =
    [
        ("Copier paper, case of 10 reams", 48.50m), ("Toner cartridge, black", 89.00m), ("Delivery service", 35.00m),
        ("Consulting hours", 125.00m), ("Maintenance visit", 180.00m), ("Catering, lunch per head", 14.75m),
        ("Office chair, mesh", 219.00m), ("Network switch, 24-port", 310.00m), ("Cleaning, per visit", 95.00m),
        ("Banner printing, per unit", 42.00m), ("Shipping pallet", 22.00m), ("Software licence, per seat", 60.00m),
    ];

    public static Corpus Generate(int seed = 42, int invoices = 20, int contracts = 5, int manuals = 3)
    {
        var rng = new Random(seed);
        var invoiceList = new List<Invoice>();
        for (var i = 0; i < invoices; i++)
        {
            var number = $"INV-{1001 + i}";
            var date = new DateOnly(2026, 7, 1).AddDays(rng.Next(0, 100));
            var termsDays = new[] { 15, 30, 45, 60 }[rng.Next(4)];
            var lineCount = rng.Next(2, 6);
            var lines = new List<InvoiceLine>();
            foreach (var idx in Enumerable.Range(0, Items.Length).OrderBy(_ => rng.Next()).Take(lineCount))
            {
                var qty = rng.Next(1, 12);
                var (desc, price) = Items[idx];
                lines.Add(new InvoiceLine(desc, qty, price, decimal.Round(qty * price, 2)));
            }
            var subtotal = lines.Sum(l => l.Amount);
            var tax = decimal.Round(subtotal * 0.07m, 2);
            invoiceList.Add(new Invoice(
                $"{number}.pdf", number, Vendors[rng.Next(Vendors.Length)], Customers[rng.Next(Customers.Length)],
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date.AddDays(termsDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                $"Net {termsDays}", lines, subtotal, tax, subtotal + tax));
        }

        var contractList = new List<Contract>();
        string[] laws = ["the State of Florida", "the State of Georgia", "the State of Delaware", "the State of Texas", "the State of New York"];
        for (var i = 0; i < contracts; i++)
        {
            var provider = Vendors[(i * 3) % Vendors.Length];
            var client = Customers[i % Customers.Length];
            contractList.Add(new Contract(
                $"MSA-{provider.Split(' ')[0]}-{client.Split(' ')[0]}.pdf", provider, client,
                new DateOnly(2026, 1, 15).AddMonths(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TermMonths: 12 + 12 * (i % 3), PaymentTerms: $"Net {new[] { 30, 45, 60 }[i % 3]}",
                TerminationNoticeDays: new[] { 30, 60, 90 }[i % 3], GoverningLaw: laws[i % laws.Length],
                LiabilityCapUsd: 50_000m * (i + 1), SlaPercent: new[] { 99.5m, 99.9m, 99.0m }[i % 3], MonthlyFeeUsd: 1_250m + 375m * i));
        }

        var manualList = new List<Manual>();
        (string Product, string Model)[] products = [("Dehumidifier", "DH-200"), ("Air Purifier", "AP-450"), ("Space Heater", "SH-1500")];
        for (var i = 0; i < manuals; i++)
        {
            var (product, model) = products[i % products.Length];
            manualList.Add(new Manual(
                $"Manual-{model}.pdf", product, model,
                PowerWatts: new[] { 350, 60, 1500 }[i % 3], CapacityPintsPerDay: new[] { 50, 0, 0 }[i % 3],
                MinTempC: 5, MaxTempC: new[] { 32, 40, 30 }[i % 3], FilterMonths: new[] { 3, 6, 12 }[i % 3],
                WarrantyYears: new[] { 2, 3, 1 }[i % 3], WeightKg: new[] { 14.5m, 6.2m, 3.8m }[i % 3],
                ErrorCodes:
                [
                    new ErrorCode("E1", "the water tank is full or missing"),
                    new ErrorCode("E2", "the room temperature is outside the operating range"),
                    new ErrorCode("E3", "the filter needs cleaning or replacement"),
                    new ErrorCode("E4", "the humidity sensor has failed"),
                ]));
        }
        return new Corpus(invoiceList, contractList, manualList);
    }

    // ------------------------------------------------------------------ rendering

    private static string Money(decimal v) => "$" + v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static byte[] RenderInvoice(Invoice inv) => Document.Create(c => c.Page(page =>
    {
        page.Size(PageSizes.Letter);
        page.Margin(40);
        page.DefaultTextStyle(x => x.FontSize(11));
        page.Content().Column(col =>
        {
            col.Spacing(6);
            col.Item().Text("INVOICE").FontSize(22).Bold();
            col.Item().Text($"Invoice Number: {inv.Number}");
            col.Item().Text($"Invoice Date: {inv.InvoiceDate}");
            col.Item().Text($"Due Date: {inv.DueDate}");
            col.Item().Text($"Payment Terms: {inv.Terms}");
            col.Item().PaddingTop(8).Text($"From: {inv.Vendor}");
            col.Item().Text("1200 Bayshore Boulevard, Tampa, FL 33606");
            col.Item().PaddingTop(4).Text($"Bill To: {inv.Customer}");
            col.Item().Text("500 Harbor Island Drive, Tampa, FL 33602");
            col.Item().PaddingTop(10).Text("Line items").Bold();
            foreach (var line in inv.Lines)
            {
                col.Item().Text($"{line.Description}; quantity {line.Quantity}; unit price {Money(line.UnitPrice)}; amount {Money(line.Amount)}");
            }
            col.Item().PaddingTop(10).Text($"Subtotal: {Money(inv.Subtotal)}");
            col.Item().Text($"Sales Tax (7%): {Money(inv.Tax)}");
            col.Item().Text($"Total Due: {Money(inv.Total)}").Bold();
            col.Item().PaddingTop(14).Text("Thank you for your business. Please reference the invoice number with your payment.").FontSize(9);
        });
    })).GeneratePdf();

    public static byte[] RenderContract(Contract k) => Document.Create(c => c.Page(page =>
    {
        page.Size(PageSizes.Letter);
        page.Margin(50);
        page.DefaultTextStyle(x => x.FontSize(11));
        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Text("MASTER SERVICES AGREEMENT").FontSize(18).Bold();
            col.Item().Text($"This Master Services Agreement (the \"Agreement\") is entered into as of {k.EffectiveDate} (the \"Effective Date\") between {k.Provider} (\"Provider\") and {k.Client} (\"Client\").");
            col.Item().Text("1. Services").Bold();
            col.Item().Text("Provider will deliver the services described in each statement of work executed under this Agreement.");
            col.Item().Text("2. Term").Bold();
            col.Item().Text($"The initial term of this Agreement is {k.TermMonths} months from the Effective Date and renews for successive twelve-month periods unless either party gives notice of non-renewal.");
            col.Item().Text("3. Fees and Payment").Bold();
            col.Item().Text($"Client will pay Provider a monthly fee of {Money(k.MonthlyFeeUsd)}. Invoices are due {k.PaymentTerms} days from the invoice date. Payment terms: {k.PaymentTerms}.");
            col.Item().Text("4. Service Levels").Bold();
            col.Item().Text($"Provider will maintain a monthly service availability of at least {k.SlaPercent.ToString(CultureInfo.InvariantCulture)} percent, measured per calendar month and excluding scheduled maintenance.");
            col.Item().Text("5. Termination").Bold();
            col.Item().Text($"Either party may terminate this Agreement for convenience with {k.TerminationNoticeDays} days written notice. Either party may terminate for material breach not cured within thirty days of notice.");
            col.Item().Text("6. Limitation of Liability").Bold();
            col.Item().Text($"Except for breaches of confidentiality, each party's aggregate liability under this Agreement is capped at {Money(k.LiabilityCapUsd)}.");
            col.Item().Text("7. Governing Law").Bold();
            col.Item().Text($"This Agreement is governed by the laws of {k.GoverningLaw}, without regard to its conflict of laws rules.");
            col.Item().Text("8. Entire Agreement").Bold();
            col.Item().Text("This Agreement, together with its statements of work, is the entire agreement between the parties on its subject matter.");
        });
    })).GeneratePdf();

    public static byte[] RenderManual(Manual m) => Document.Create(c =>
    {
        c.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(50);
            page.DefaultTextStyle(x => x.FontSize(11));
            page.Content().Column(col =>
            {
                col.Spacing(8);
                col.Item().Text($"{m.Product} {m.Model} User Manual").FontSize(18).Bold();
                col.Item().Text("Safety Information").Bold();
                col.Item().Text("Read all instructions before use. Do not operate the unit with a damaged cord. Keep the air inlets and outlets clear. Unplug the unit before cleaning.");
                col.Item().Text("Specifications").Bold();
                col.Item().Text($"Model: {m.Model}");
                col.Item().Text($"Rated power consumption: {m.PowerWatts} W");
                if (m.CapacityPintsPerDay > 0)
                {
                    col.Item().Text($"Moisture removal capacity: {m.CapacityPintsPerDay} pints per day");
                }
                col.Item().Text($"Operating temperature range: {m.MinTempC} to {m.MaxTempC} degrees C");
                col.Item().Text($"Filter replacement interval: every {m.FilterMonths} months");
                col.Item().Text($"Net weight: {m.WeightKg.ToString(CultureInfo.InvariantCulture)} kg");
                col.Item().Text($"Warranty: {m.WarrantyYears} years limited warranty");
            });
        });
        c.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(50);
            page.DefaultTextStyle(x => x.FontSize(11));
            page.Content().Column(col =>
            {
                col.Spacing(8);
                col.Item().Text("Troubleshooting").FontSize(16).Bold();
                col.Item().Text("If the display shows an error code, find it below before contacting support.");
                foreach (var e in m.ErrorCodes)
                {
                    col.Item().Text($"Error code {e.Code} means {e.Meaning}.");
                }
                col.Item().Text("Maintenance").Bold();
                col.Item().Text($"Clean the housing with a dry cloth. Replace the filter every {m.FilterMonths} months or sooner in dusty environments.");
            });
        });
    }).GeneratePdf();

    // ------------------------------------------------------------------ questions

    public static IReadOnlyList<EvalQuestion> Questions(Corpus corpus)
    {
        var qs = new List<EvalQuestion>();
        var n = 1;
        string Id() => $"q{n++:00}";
        foreach (var inv in corpus.Invoices.Take(8))
        {
            qs.Add(new EvalQuestion(Id(), "invoice_total", $"What is the total amount due on invoice {inv.Number}?", inv.Total.ToString("0.00", CultureInfo.InvariantCulture), inv.FileName));
        }
        foreach (var inv in corpus.Invoices.Skip(8).Take(4))
        {
            qs.Add(new EvalQuestion(Id(), "invoice_vendor", $"Who issued invoice {inv.Number}?", inv.Vendor, inv.FileName));
        }
        foreach (var inv in corpus.Invoices.Skip(12).Take(4))
        {
            qs.Add(new EvalQuestion(Id(), "invoice_due_date", $"When is invoice {inv.Number} due?", inv.DueDate, inv.FileName));
        }
        foreach (var inv in corpus.Invoices.Skip(16).Take(2))
        {
            qs.Add(new EvalQuestion(Id(), "invoice_tax", $"How much sales tax was charged on invoice {inv.Number}?", inv.Tax.ToString("0.00", CultureInfo.InvariantCulture), inv.FileName));
        }
        foreach (var inv in corpus.Invoices.Skip(18).Take(2))
        {
            qs.Add(new EvalQuestion(Id(), "invoice_customer", $"Which customer is billed on invoice {inv.Number}?", inv.Customer, inv.FileName));
        }
        foreach (var k in corpus.Contracts)
        {
            qs.Add(new EvalQuestion(Id(), "contract_notice", $"How many days of notice are required to terminate the agreement between {k.Provider} and {k.Client} for convenience?", k.TerminationNoticeDays.ToString(CultureInfo.InvariantCulture), k.FileName));
            qs.Add(new EvalQuestion(Id(), "contract_fee", $"What is the monthly fee in the agreement between {k.Provider} and {k.Client}?", k.MonthlyFeeUsd.ToString("0.00", CultureInfo.InvariantCulture), k.FileName));
        }
        var k0 = corpus.Contracts[0];
        qs.Add(new EvalQuestion(Id(), "contract_law", $"Which state's law governs the agreement between {k0.Provider} and {k0.Client}?", k0.GoverningLaw.Replace("the State of ", ""), k0.FileName));
        qs.Add(new EvalQuestion(Id(), "contract_sla", $"What service availability does {k0.Provider} commit to for {k0.Client}?", k0.SlaPercent.ToString(CultureInfo.InvariantCulture), k0.FileName));
        foreach (var m in corpus.Manuals)
        {
            qs.Add(new EvalQuestion(Id(), "manual_spec", $"What is the rated power consumption of the {m.Model}?", m.PowerWatts.ToString(CultureInfo.InvariantCulture), m.FileName));
            qs.Add(new EvalQuestion(Id(), "manual_spec", $"What is the maximum operating temperature of the {m.Model}?", m.MaxTempC.ToString(CultureInfo.InvariantCulture), m.FileName));
            qs.Add(new EvalQuestion(Id(), "manual_error", $"What does error code E2 mean on the {m.Model}?", "operating range", m.FileName, ["outside the operating range", "temperature is outside"]));
            qs.Add(new EvalQuestion(Id(), "manual_maintenance", $"How often should the filter of the {m.Model} be replaced?", m.FilterMonths.ToString(CultureInfo.InvariantCulture), m.FileName));
        }
        qs.Add(new EvalQuestion(Id(), "unanswerable", "What is the CEO's home address?", null, null, ["not contain", "none of the", "no information", "not found", "cannot find", "do not have", "does not mention", "not mentioned", "not available", "no mention", "not provided", "not included", "unable to"]));
        qs.Add(new EvalQuestion(Id(), "unanswerable", "What was the stock price of Northwind Supply Co. last Friday?", null, null, ["not contain", "none of the", "no information", "not found", "cannot find", "do not have", "does not mention", "not mentioned", "not available", "no mention", "not provided", "not included", "unable to"]));
        return qs;
    }
}
